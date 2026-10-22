using System.Net;
using System.Security.Cryptography.X509Certificates;
using Kiroku.Core;

namespace Kiroku.Server;

public sealed record ServerOptions(DataPaths Paths, IKeyProtector Protector, int HttpsPort = Ports.Https,
    int DiscoveryPort = Ports.Discovery, bool BindAny = true, bool Background = true);

public sealed class ConfigHolder(DataPaths paths)
{
    public ServerConfig Current { get; private set; } = ServerConfig.Load(paths);
    public void Update(Action<ServerConfig> f) { f(Current); Current.Save(paths); }
}

public sealed record Session(Actor Actor, StaffInfo Staff, string Token, bool MustChange);

public static class ServerHost
{
    public const string Version = "0.1.0";

    public static WebApplication Build(ServerOptions o, string[] args)
    {
        o.Paths.Ensure(); o.Paths.CleanTmp();
        var keys = new KeyStore(o.Paths, o.Protector);
        var store = new KirokuStore(o.Paths.Db, keys.LoadOrCreateDbKey());
        var cert = Certs.LoadOrCreate(o.Paths, o.Protector);
        var fp = Certs.Fingerprint(cert);
        var cfg = new ConfigHolder(o.Paths);
        var backups = new BackupService(o.Paths, store, keys, () => cfg.Current);
        var health = new BackupHealth();

        var b = WebApplication.CreateBuilder(args);
        b.Host.UseWindowsService(s => s.ServiceName = "KirokuServer");
        b.Logging.ClearProviders();
        b.Logging.AddConsole();
        if (OperatingSystem.IsWindows()) b.Logging.AddEventLog();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.WebHost.ConfigureKestrel(k =>
        {
            var ip = o.BindAny ? IPAddress.Any : IPAddress.Loopback;
            k.Listen(ip, o.HttpsPort, l => l.UseHttps(cert));
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 1 << 20;
        });
        b.Services.AddSingleton(store).AddSingleton(keys).AddSingleton(backups).AddSingleton(health).AddSingleton(cfg);
        b.Services.AddSingleton<Func<ServerConfig>>(_ => () => cfg.Current);
        if (o.Background)
        {
            b.Services.AddHostedService(sp => new DiscoveryService(() => cfg.Current, fp, o.HttpsPort, o.DiscoveryPort, sp.GetRequiredService<ILogger<DiscoveryService>>()));
            b.Services.AddHostedService<BackupWorker>();
        }
        var app = b.Build();
        app.Lifetime.ApplicationStopped.Register(store.Dispose);

        // 事業所内LAN以外からの接続は常に拒否
        app.Use(async (ctx, next) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip == null || !Lan.IsAllowed(ip)) { ctx.Response.StatusCode = 403; return; }
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            try { await next(); }
            catch (KirokuException e)
            {
                ctx.Response.StatusCode = e.Status;
                await ctx.Response.WriteAsJsonAsync(new ApiError(e.Code, e.Message, e.Detail));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 内容(個人情報を含みうる)はログに出さず、型名だけ記録する
                app.Logger.LogError("想定外のエラー: {Type}", e.GetType().Name);
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new ApiError("internal", "親機で想定外のエラーが起きました。もう一度試し、続く場合は管理者に連絡してください。"));
            }
        });

        string DeviceId(HttpContext c)
        {
            var tok = c.Request.Headers["X-Device-Token"].ToString();
            return (tok.Length > 0 ? store.ValidateDevice(tok) : null)
                ?? throw new KirokuException(401, "device_unregistered", "この端末は親機に登録されていないか、登録が取り消されました。管理者に連絡してください。");
        }

        Session Auth(HttpContext c, bool admin = false, bool allowMust = false)
        {
            var dev = DeviceId(c);
            var h = c.Request.Headers.Authorization.ToString();
            var tok = h.StartsWith("Bearer ") ? h[7..] : "";
            var s = (tok.Length > 0 ? store.ValidateSession(tok, dev) : null)
                ?? throw new KirokuException(401, "not_logged_in", "ログインが切れました。もう一度ログインしてください。");
            if (s.MustChange && !allowMust)
                throw new KirokuException(403, "must_change_password", "初期パスワードのままです。新しいパスワードに変更してください。");
            if (admin && !s.Staff.IsAdmin)
                throw new KirokuException(403, "forbidden", "この操作は管理者だけが行えます。");
            return new Session(new Actor(s.Staff.Id, s.Staff.Name, dev), s.Staff, tok, s.MustChange);
        }

        var api = app.MapGroup("/api");

        // ---- 認証前に使えるもの ----
        api.MapGet("/hello", () => new HelloInfo(cfg.Current.ServerName, fp, store.IsInitialized(), Version));

        api.MapPost("/setup", (HttpContext c, SetupRequest r) =>
        {
            if (!IPAddress.IsLoopback(c.Connection.RemoteIpAddress!))
                throw new KirokuException(403, "local_only", "初期設定は、親機にするパソコン自身でのみ行えます。");
            var (admin, devId, token) = store.Setup(r.LoginId, r.Name, r.Password, string.IsNullOrWhiteSpace(r.DeviceName) ? "親機" : r.DeviceName);
            var rk = RecoveryKey.Generate();
            keys.StoreRecoveryWrap(RecoveryWrap.Create(store.DbKey, rk));
            try { backups.RunNow(new Actor(admin.Id, admin.Name, devId)); } catch (KirokuException) { /* 状態画面の警告で案内 */ }
            return new SetupResult(admin, token, rk, fp);
        });

        api.MapPost("/pairing/request", (PairStartRequest r) => store.StartPairing(r.DeviceName, r.Nonce, r.Proof, fp));
        api.MapGet("/pairing/status", (string id, string secret) => store.PollPairing(id, secret));

        // ---- ログイン(端末登録済みであること) ----
        api.MapPost("/login", (HttpContext c, LoginRequest r) =>
        {
            var dev = DeviceId(c);
            var (res, must) = store.Login(dev, r.LoginId, r.Password);
            return new LoginResponse(res.SessionToken, res.Staff, must);
        });
        api.MapPost("/logout", (HttpContext c) => { var s = Auth(c, allowMust: true); store.Logout(s.Actor, s.Token); return Results.NoContent(); });
        api.MapGet("/me", (HttpContext c) => Auth(c, allowMust: true).Staff);
        api.MapPost("/password/change", (HttpContext c, PasswordChangeRequest r) => { var s = Auth(c, allowMust: true); store.ChangeOwnPassword(s.Actor, r.OldPassword, r.NewPassword); return Results.NoContent(); });
        api.MapGet("/pulse", (HttpContext c) => { Auth(c); return new PulseResult(store.Revision); });

        // ---- 日々の記録 ----
        api.MapGet("/clients", (HttpContext c, string? q) => { Auth(c); return store.ListClients(q); });
        api.MapPut("/clients/{id}", (HttpContext c, string id, RenameRequest r) => { store.RenameClient(Auth(c, admin: true).Actor, id, r.Name); return Results.NoContent(); });
        api.MapGet("/records", (HttpContext c, string date) => { Auth(c); return store.ListRecordsByDate(date); });
        api.MapGet("/records/one", (HttpContext c, string clientId, string date) => { Auth(c); return store.GetRecord(clientId, date) is { } d ? Results.Ok(d) : Results.NoContent(); });
        api.MapGet("/records/search", (HttpContext c, string? name, string? from, string? to) =>
        {
            var s = Auth(c);
            var res = store.SearchRecords(name, from, to);
            if (!string.IsNullOrWhiteSpace(name)) store.WriteAudit(s.Actor, "record_search", "", $"{res.Count}件");
            return res;
        });
        api.MapGet("/records/{id}", (HttpContext c, string id) => { Auth(c); return store.GetRecord(id) is { } d ? Results.Ok(d) : Results.NotFound(new ApiError("not_found", "記録が見つかりません。")); });
        api.MapGet("/records/{id}/history", (HttpContext c, string id) => store.RecordHistory(Auth(c).Actor, id));
        api.MapPut("/records", (HttpContext c, RecordSaveRequest r) => store.SaveRecord(Auth(c).Actor, r));
        api.MapGet("/suggestions/work", (HttpContext c, string? q) => { Auth(c); return store.WorkSuggestions(q); });

        // ---- 終礼メモ ----
        api.MapGet("/memo", (HttpContext c, string date) => { Auth(c); return store.GetMemo(date); });
        api.MapPut("/memo", (HttpContext c, MemoSaveRequest r) => store.SaveMemo(Auth(c).Actor, r));
        api.MapGet("/memo/history", (HttpContext c, string date) => store.MemoHistory(Auth(c).Actor, date));

        // ---- 印刷・PDF(許可された職員のみ。実際の出力は端末側、許可と記録は親機) ----
        api.MapPost("/print/authorize", (HttpContext c, PrintAuthRequest r) =>
        {
            var s = Auth(c);
            if (!s.Staff.CanPrint) throw new KirokuException(403, "no_print_permission", "印刷・PDF出力の権限がありません。管理者に権限を依頼してください。");
            store.WriteAudit(s.Actor, "print", r.Kind, TimeText.Date(r.Date)!);
            return Results.NoContent();
        });

        // ---- 管理者 ----
        var staff = api.MapGroup("/staff");
        staff.MapGet("", (HttpContext c) => { Auth(c, admin: true); return store.ListStaff(); });
        staff.MapPost("", (HttpContext c, StaffCreateRequest r) => store.CreateStaff(Auth(c, admin: true).Actor, r.LoginId, r.Name, r.Password, r.CanPrint));
        staff.MapPost("/{id}/password", (HttpContext c, string id, PasswordSetRequest r) => { store.ResetPassword(Auth(c, admin: true).Actor, id, r.NewPassword); return Results.NoContent(); });
        staff.MapPost("/{id}/active", (HttpContext c, string id, BoolRequest r) => { store.SetStaffActive(Auth(c, admin: true).Actor, id, r.Value); return Results.NoContent(); });
        staff.MapPost("/{id}/print", (HttpContext c, string id, BoolRequest r) => { store.SetStaffCanPrint(Auth(c, admin: true).Actor, id, r.Value); return Results.NoContent(); });

        var dev = api.MapGroup("/devices");
        dev.MapGet("", (HttpContext c) => { Auth(c, admin: true); return store.ListDevices(); });
        dev.MapPost("/code", (HttpContext c) => { var (code, exp) = store.CreatePairingCode(Auth(c, admin: true).Actor); return new PairingCodeResult(code, exp.ToString("HH:mm")); });
        dev.MapPost("/{id}/approve", (HttpContext c, string id) => { store.ApproveDevice(Auth(c, admin: true).Actor, id); return Results.NoContent(); });
        dev.MapPost("/{id}/reject", (HttpContext c, string id) => { store.RejectDevice(Auth(c, admin: true).Actor, id); return Results.NoContent(); });
        dev.MapPost("/{id}/revoke", (HttpContext c, string id) => { store.RevokeDevice(Auth(c, admin: true).Actor, id); return Results.NoContent(); });

        api.MapGet("/audit", (HttpContext c, int? limit, long? before) => { Auth(c, admin: true); return store.ListAudit(limit ?? 300, before); });
        api.MapGet("/audit/verify", (HttpContext c) => { Auth(c, admin: true); var (ok, bad) = store.VerifyAudit(); return new { ok, badSeq = bad }; });

        var bk = api.MapGroup("/backup");
        BackupStatus WithHealth() { var st = backups.Status(); return health.LastError == null ? st : st with { Warnings = [.. st.Warnings, "自動バックアップが失敗しました: " + health.LastError] }; }
        bk.MapGet("/status", (HttpContext c) => { Auth(c, admin: true); return WithHealth(); });
        bk.MapPost("/run", (HttpContext c) => { backups.RunNow(Auth(c, admin: true).Actor); health.LastError = null; return WithHealth(); });
        bk.MapPost("/verify", (HttpContext c) => { var s = Auth(c, admin: true); var r = backups.VerifyAll().Select(x => new VerifyItem(x.File.Name, x.File.Location, x.Ok)).ToList(); store.WriteAudit(s.Actor, "backup_verify", "", $"{r.Count(x => x.Ok)}/{r.Count}件OK"); return r; });
        bk.MapPost("/config", (HttpContext c, BackupConfigRequest r) =>
        {
            var s = Auth(c, admin: true);
            var path = string.IsNullOrWhiteSpace(r.ExternalPath) ? null : r.ExternalPath.Trim();
            if (path != null && !BackupService.IsReachable(path))
                throw new KirokuException(400, "bad_path", "そのフォルダには保存できません。USBメモリ等が挿さっているか、書き込み可能かを確認してください。");
            cfg.Update(x => x.ExternalBackupPath = path);
            store.WriteAudit(s.Actor, "backup_config", "", path == null ? "別媒体の指定を解除" : "別媒体の保存先を設定");
            return WithHealth();
        });
        bk.MapPost("/restore", (HttpContext c, RestoreRequest r) => { backups.Restore(Auth(c, admin: true).Actor, r.Location, r.FileName, r.RecoveryKey); return Results.NoContent(); });
        bk.MapPost("/recovery-key", (HttpContext c) => new RecoveryKeyResult(backups.ReissueRecoveryKey(Auth(c, admin: true).Actor)));

        api.MapGet("/system/status", (HttpContext c) =>
        {
            Auth(c, admin: true);
            var (ac, dv) = store.Counts();
            var st = backups.Status();
            return new SystemStatus(Version, cfg.Current.ServerName, store.IntegrityOk(), ac, dv, st.LastExternalAt ?? st.LastLocalAt, store.VerifyAudit().Ok, store.Revision);
        });

        app.Services.GetRequiredService<ILoggerFactory>(); // 早期初期化
        app.Urls.Clear();
        return app;
    }
}
