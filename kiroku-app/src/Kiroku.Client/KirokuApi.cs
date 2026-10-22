using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Kiroku.Core;

namespace Kiroku.Client;

/// <summary>親機との通信。TLSは親機証明書の指紋でピン留めし、一致しない相手には一切つながない。</summary>
public sealed class KirokuApi : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    public string? SessionToken { get; set; }
    public string Host { get; }
    public int Port { get; }

    public KirokuApi(string host, int port, string pinnedFingerprint, string? deviceToken)
    {
        Host = host; Port = port;
        var h = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert != null && string.Equals(Hashing.Sha256Hex(cert.GetRawCertData()), pinnedFingerprint, StringComparison.OrdinalIgnoreCase)
            }
        };
        _http = new HttpClient(h) { BaseAddress = new Uri($"https://{(host.Contains(':') ? $"[{host}]" : host)}:{port}/"), Timeout = TimeSpan.FromSeconds(12) };
        if (!string.IsNullOrEmpty(deviceToken)) _http.DefaultRequestHeaders.Add("X-Device-Token", deviceToken);
    }

    private HttpRequestMessage Req(HttpMethod m, string path, object? body)
    {
        var r = new HttpRequestMessage(m, path);
        if (SessionToken != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);
        if (body != null) r.Content = JsonContent.Create(body, options: J);
        return r;
    }

    public async Task<T> Send<T>(HttpMethod m, string path, object? body = null, CancellationToken ct = default)
    {
        var res = await SendRaw(m, path, body, ct);
        if (res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0) return default!;
        return (await res.Content.ReadFromJsonAsync<T>(J, ct))!;
    }

    public async Task Send(HttpMethod m, string path, object? body = null, CancellationToken ct = default) => await SendRaw(m, path, body, ct);

    private async Task<HttpResponseMessage> SendRaw(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await _http.SendAsync(Req(m, path, body), ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or SocketException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new KirokuException(0, "unreachable", "親機に接続できません。親機の電源とWi-Fi接続を確認してください。入力中の内容は画面に残っています(まだ保存されていません)。");
        }
        if (res.IsSuccessStatusCode) return res;
        ApiError? err = null;
        try { err = await res.Content.ReadFromJsonAsync<ApiError>(J, ct); } catch { }
        throw new KirokuException((int)res.StatusCode, err?.Code ?? "http_" + (int)res.StatusCode, err?.Message ?? $"親機からエラーが返されました({(int)res.StatusCode})。",
            err?.Detail is JsonElement el ? el.Clone() : err?.Detail);
    }

    private static string Q(string s) => Uri.EscapeDataString(s);

    // ---- 認証 ----
    public Task<HelloInfo> Hello() => Send<HelloInfo>(HttpMethod.Get, "api/hello");
    public async Task<LoginResponse> Login(string loginId, string password)
    {
        var r = await Send<LoginResponse>(HttpMethod.Post, "api/login", new LoginRequest(loginId, password));
        SessionToken = r.SessionToken; return r;
    }
    public async Task Logout() { try { await Send(HttpMethod.Post, "api/logout"); } finally { SessionToken = null; } }
    public Task<StaffInfo> Me() => Send<StaffInfo>(HttpMethod.Get, "api/me");
    public Task ChangePassword(string oldPw, string newPw) => Send(HttpMethod.Post, "api/password/change", new PasswordChangeRequest(oldPw, newPw));
    public Task<PulseResult> Pulse(CancellationToken ct = default) => Send<PulseResult>(HttpMethod.Get, "api/pulse", null, ct);

    // ---- 記録 ----
    public Task<List<ClientInfo>> Clients(string? q = null) => Send<List<ClientInfo>>(HttpMethod.Get, "api/clients" + (q == null ? "" : "?q=" + Q(q)));
    public Task<List<RecordSummary>> Records(string date) => Send<List<RecordSummary>>(HttpMethod.Get, "api/records?date=" + Q(date));
    public Task<RecordDetail?> Record(string clientId, string date) => Send<RecordDetail?>(HttpMethod.Get, $"api/records/one?clientId={Q(clientId)}&date={Q(date)}");
    public Task<RecordDetail> GetRecord(string id) => Send<RecordDetail>(HttpMethod.Get, "api/records/" + Q(id));
    public Task<RecordDetail> SaveRecord(RecordSaveRequest r) => Send<RecordDetail>(HttpMethod.Put, "api/records", r);
    public Task<List<RecordHistoryItem>> RecordHistory(string id) => Send<List<RecordHistoryItem>>(HttpMethod.Get, $"api/records/{Q(id)}/history");
    public Task<List<RecordSummary>> Search(string? name, string? from, string? to) =>
        Send<List<RecordSummary>>(HttpMethod.Get, $"api/records/search?name={Q(name ?? "")}&from={Q(from ?? "")}&to={Q(to ?? "")}");
    public Task<List<string>> WorkSuggestions(string? q) => Send<List<string>>(HttpMethod.Get, "api/suggestions/work?q=" + Q(q ?? ""));
    public Task RenameClient(string id, string name) => Send(HttpMethod.Put, "api/clients/" + Q(id), new RenameRequest(name));

    // ---- 終礼メモ ----
    public Task<MemoDetail> Memo(string date) => Send<MemoDetail>(HttpMethod.Get, "api/memo?date=" + Q(date));
    public Task<MemoDetail> SaveMemo(MemoSaveRequest r) => Send<MemoDetail>(HttpMethod.Put, "api/memo", r);
    public Task<List<MemoHistoryItem>> MemoHistory(string date) => Send<List<MemoHistoryItem>>(HttpMethod.Get, "api/memo/history?date=" + Q(date));
    public Task AuthorizePrint(string kind, string date) => Send(HttpMethod.Post, "api/print/authorize", new PrintAuthRequest(kind, date));

    // ---- 管理 ----
    public Task<List<StaffInfo>> Staff() => Send<List<StaffInfo>>(HttpMethod.Get, "api/staff");
    public Task<StaffInfo> CreateStaff(StaffCreateRequest r) => Send<StaffInfo>(HttpMethod.Post, "api/staff", r);
    public Task ResetPassword(string id, string pw) => Send(HttpMethod.Post, $"api/staff/{id}/password", new PasswordSetRequest(pw));
    public Task SetActive(string id, bool v) => Send(HttpMethod.Post, $"api/staff/{id}/active", new BoolRequest(v));
    public Task SetCanPrint(string id, bool v) => Send(HttpMethod.Post, $"api/staff/{id}/print", new BoolRequest(v));
    public Task<List<DeviceInfo>> Devices() => Send<List<DeviceInfo>>(HttpMethod.Get, "api/devices");
    public Task<PairingCodeResult> NewPairingCode() => Send<PairingCodeResult>(HttpMethod.Post, "api/devices/code");
    public Task ApproveDevice(string id) => Send(HttpMethod.Post, $"api/devices/{id}/approve");
    public Task RejectDevice(string id) => Send(HttpMethod.Post, $"api/devices/{id}/reject");
    public Task RevokeDevice(string id) => Send(HttpMethod.Post, $"api/devices/{id}/revoke");
    public Task<List<AuditItem>> Audit(int limit = 300) => Send<List<AuditItem>>(HttpMethod.Get, "api/audit?limit=" + limit);
    public Task<BackupStatus> BackupStatus() => Send<BackupStatus>(HttpMethod.Get, "api/backup/status");
    public Task<BackupStatus> RunBackup() => Send<BackupStatus>(HttpMethod.Post, "api/backup/run");
    public Task<List<VerifyItem>> VerifyBackups() => Send<List<VerifyItem>>(HttpMethod.Post, "api/backup/verify");
    public Task<BackupStatus> SetBackupPath(string? path) => Send<BackupStatus>(HttpMethod.Post, "api/backup/config", new BackupConfigRequest(path));
    public Task Restore(string location, string file, string recoveryKey) => Send(HttpMethod.Post, "api/backup/restore", new RestoreRequest(location, file, recoveryKey));
    public Task<RecoveryKeyResult> ReissueRecoveryKey() => Send<RecoveryKeyResult>(HttpMethod.Post, "api/backup/recovery-key");
    public Task<SystemStatus> SystemStatus() => Send<SystemStatus>(HttpMethod.Get, "api/system/status");

    public void Dispose() => _http.Dispose();
}

/// <summary>初回接続(親機の初期設定・子機の登録)。証明書は接続コードの証明(HMAC)で親機本人と確認できた場合のみ採用する。</summary>
public static class Pairing
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    private sealed class Probe : IDisposable
    {
        public readonly HttpClient Http;
        public string? Fingerprint; public bool Mixed;
        public Probe(string host, int port)
        {
            var h = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(4), MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    {
                        if (cert == null) return false;
                        var f = Hashing.Sha256Hex(cert.GetRawCertData());
                        if (Fingerprint != null && Fingerprint != f) Mixed = true;
                        Fingerprint = f; return true; // ここでは受け入れるが、証明(HMAC)が通らなければ採用しない
                    }
                }
            };
            Http = new HttpClient(h) { BaseAddress = new Uri($"https://{(host.Contains(':') ? $"[{host}]" : host)}:{port}/"), Timeout = TimeSpan.FromSeconds(12) };
        }
        public void Dispose() => Http.Dispose();
    }

    public sealed record ClientPairResult(string Fingerprint, string DeviceToken);

    /// <summary>親機のパソコン自身で初期設定(管理者登録)。127.0.0.1の自分のサービスにだけ接続する。</summary>
    public static async Task<SetupResult> SetupParent(int port, SetupRequest req, string expectedFingerprintOrEmpty = "")
    {
        using var p = new Probe("127.0.0.1", port);
        try
        {
            var res = await p.Http.PostAsJsonAsync("api/setup", req, J);
            if (!res.IsSuccessStatusCode)
            {
                var e = await res.Content.ReadFromJsonAsync<ApiError>(J);
                throw new KirokuException((int)res.StatusCode, e?.Code ?? "setup", e?.Message ?? "初期設定に失敗しました。");
            }
            return (await res.Content.ReadFromJsonAsync<SetupResult>(J))!;
        }
        catch (HttpRequestException)
        {
            throw new KirokuException(0, "service_down", "共有サービスに接続できません。インストールが完了しているか、しばらく待ってからもう一度お試しください。");
        }
    }

    public static async Task<HelloInfo> HelloLocal(int port)
    {
        using var p = new Probe("127.0.0.1", port);
        try { return (await p.Http.GetFromJsonAsync<HelloInfo>("api/hello", J))!; }
        catch (HttpRequestException) { throw new KirokuException(0, "service_down", "共有サービスに接続できません。"); }
    }

    /// <summary>子機の登録申請→親機での承認待ち→端末トークン受領。</summary>
    public static async Task<ClientPairResult> PairChild(string host, int port, string code, string deviceName,
        Action<string>? progress = null, TimeSpan? waitLimit = null, CancellationToken ct = default)
    {
        using var p = new Probe(host, port);
        var nonce = Rand.Token(16);
        PairingStartResult start;
        try
        {
            // 証明書を見るためのダミー接続(指紋の確定)
            await p.Http.GetAsync("api/hello", ct);
            var f = p.Fingerprint ?? throw new KirokuException(0, "no_cert", "親機の証明書を確認できませんでした。");
            var proof = Hashing.HmacHex(code, $"pair|{nonce}|{f}");
            var res = await p.Http.PostAsJsonAsync("api/pairing/request", new PairStartRequest(deviceName, nonce, proof), J, ct);
            if (!res.IsSuccessStatusCode)
            {
                var e = await res.Content.ReadFromJsonAsync<ApiError>(J, ct);
                throw new KirokuException((int)res.StatusCode, e?.Code ?? "pair", e?.Message ?? "接続に失敗しました。");
            }
            start = (await res.Content.ReadFromJsonAsync<PairingStartResult>(J, ct))!;
            if (p.Mixed || p.Fingerprint != f || !Hashing.FixedEquals(start.ServerProof, Hashing.HmacHex(code, $"server|{nonce}|{f}")))
                throw new KirokuException(400, "mitm", "接続先が親機本人であることを確認できませんでした。接続を中止しました。");
        }
        catch (HttpRequestException)
        {
            throw new KirokuException(0, "unreachable", "親機に接続できません。親機の電源・Wi-Fi・アドレスを確認してください。");
        }

        var fp = p.Fingerprint!;
        progress?.Invoke("親機での承認を待っています。親機の「管理」→「接続端末」で承認してください。");
        var until = DateTime.UtcNow + (waitLimit ?? TimeSpan.FromMinutes(10));
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            var st = await p.Http.GetFromJsonAsync<PairingStatus>($"api/pairing/status?id={Uri.EscapeDataString(start.RequestId)}&secret={Uri.EscapeDataString(start.PollSecret)}", J, ct);
            switch (st!.Status)
            {
                case "approved": return new ClientPairResult(fp, st.DeviceToken!);
                case "rejected": throw new KirokuException(403, "rejected", "親機で接続が拒否されました。");
                case "lost": throw new KirokuException(410, "lost", "承認は完了しましたが、親機が再起動したため受け取れませんでした。もう一度最初から登録してください。");
            }
            await Task.Delay(1500, ct);
        }
        throw new KirokuException(408, "timeout", "承認がありませんでした(10分)。もう一度最初からやり直してください。");
    }
}
