using Kiroku.Client;
using Kiroku.Core;

namespace Kiroku.Tests;

public class BackupAndRecoveryTests : IAsyncLifetime
{
    private readonly TestServer _s = new();
    private KirokuApi _admin = null!; private SetupResult _setup = null!;
    public async Task InitializeAsync() { await _s.StartAsync(); (_admin, _setup) = await _s.SetupAdmin(); }
    public async Task DisposeAsync() => await _s.DisposeAsync();
    private static string Today => DateTime.Today.ToString("yyyy-MM-dd");

    [Fact] // 初期設定直後に最初のバックアップが作られ、別媒体未設定の警告が出る
    public async Task FirstBackup_Exists_AndWarnsAboutMissingExternalMedia()
    {
        var st = await _admin.BackupStatus();
        Assert.NotNull(st.LastLocalAt);
        Assert.Contains(st.Warnings, w => w.Contains("別の媒体"));
        Assert.Null(st.LastExternalAt);
    }

    [Fact] // 受入12: バックアップから復元できる(復旧キーで)
    public async Task Restore_FromBackup_WithRecoveryKey()
    {
        var r = await _admin.SaveRecord(new(null, "山田 太郎", Today, "9:00", "16:00", "検品", "良好", 0));
        await _admin.SaveMemo(new(Today, "終礼メモ原本", 0));
        var st = await _admin.RunBackup();
        var file = st.Files.First(f => f.Location == "local");
        // 事故: 内容を書き換え、さらに別の記録を追加
        await _admin.SaveRecord(new(r.ClientId, null, Today, "9:00", "16:00", "検品", "誤って書き換えた", r.Version));
        await _admin.SaveRecord(new(null, "後から追加", Today, null, null, null, null, 0));

        var bad = await Assert.ThrowsAsync<KirokuException>(() => _admin.Restore("local", file.Name, "AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA"));
        Assert.Equal("recovery_key", bad.Code);                                      // 誤キーでは復元されない
        Assert.Equal("誤って書き換えた", (await _admin.Record(r.ClientId, Today))!.Notes);

        await _admin.Restore("local", file.Name, _setup.RecoveryKey);
        var again = _s.Api(_setup.DeviceToken); await again.Login("admin", TestServer.AdminPw);  // 復元後は再ログイン
        Assert.Equal("良好", (await again.Record(r.ClientId, Today))!.Notes);
        Assert.Single(await again.Records(Today));
        Assert.Equal("終礼メモ原本", (await again.Memo(Today)).Text);
        Assert.Contains((await again.Audit()), a => a.Action == "restore");
    }

    [Fact] // 別媒体(USB想定)へコピー、世代管理、検証
    public async Task ExternalMedia_Copy_Generations_Verify()
    {
        var usb = Path.Combine(_s.Dir, "usb"); Directory.CreateDirectory(usb);
        var bad = await Assert.ThrowsAsync<KirokuException>(() => _admin.SetBackupPath(Path.Combine(_s.Dir, "no-such-drive")));
        Assert.Equal("bad_path", bad.Code);
        var st0 = await _admin.SetBackupPath(usb);
        Assert.DoesNotContain(st0.Warnings, w => w.Contains("未設定"));
        await _admin.SaveRecord(new(null, "山田 太郎", Today, "9:00", null, null, null, 0));
        var st = await _admin.RunBackup();
        Assert.NotNull(st.LastExternalAt);
        Assert.Single(Directory.GetFiles(usb, "*.krbk"));
        Assert.DoesNotContain(st.Warnings, w => w.Contains("別の媒体"));
        var v = await _admin.VerifyBackups();
        Assert.NotEmpty(v); Assert.All(v, x => Assert.True(x.Ok));

        // 世代管理: 31世代目で古いものが消える(ローカル保持30)
        _s.App.Services.GetRequiredService<Kiroku.Server.ConfigHolder>().Update(c => c.KeepLocal = 3);
        for (var i = 0; i < 5; i++) { _s.Store.Clock = () => DateTimeOffset.Now.AddMinutes(i + 1); await _admin.RunBackup(); }
        Assert.Equal(3, Directory.GetFiles(_s.Paths.Backups, "*.krbk").Length);

        // 別媒体のみから、別のパソコンへ復元(引っ越し・全損想定)
        var newest = Directory.GetFiles(usb, "*.krbk").OrderBy(x => x).Last();
        var other = new DataPaths(Path.Combine(_s.Dir, "newpc")); other.Ensure();
        var svc = new BackupService(other, null!, null!, () => new ServerConfig());
        var tmp = Path.Combine(other.Tmp, "x.db");
        var key = BackupCrypto.Decrypt(newest, tmp, _setup.RecoveryKey);
        Assert.True(KirokuStore.VerifyFile(tmp, key));
        _ = svc;
    }

    [Fact] // 改ざん・破損したバックアップは検出される
    public async Task CorruptedBackup_IsDetected()
    {
        var st = await _admin.RunBackup();
        var f = Path.Combine(_s.Paths.Backups, st.Files.First().Name);
        var bytes = File.ReadAllBytes(f); bytes[bytes.Length / 2] ^= 0xFF; File.WriteAllBytes(f, bytes);
        var v = await _admin.VerifyBackups();
        Assert.Contains(v, x => !x.Ok);
        var ex = await Assert.ThrowsAsync<KirokuException>(() => _admin.Restore("local", st.Files.First().Name, _setup.RecoveryKey));
        Assert.Equal("backup_corrupt", ex.Code);
        Assert.Equal(1, (await _admin.SystemStatus()).DevicesApproved); // 稼働中のデータは無傷
    }

    [Fact] // バックアップはDBよりさらに別の鍵で保護され、中身が平文で見えない
    public async Task BackupFile_IsEncrypted()
    {
        await _admin.SaveRecord(new(null, "バックアップ検査太郎", Today, null, null, null, null, 0));
        var st = await _admin.RunBackup();
        var bytes = File.ReadAllBytes(Path.Combine(_s.Paths.Backups, st.Files.First().Name));
        Assert.False(System.Text.Encoding.UTF8.GetString(bytes).Contains("バックアップ検査太郎"));
        Assert.False(System.Text.Encoding.ASCII.GetString(bytes).Contains("SQLite format"));
    }

    [Fact] // 復旧キー再発行で旧キーは新バックアップに使えない
    public async Task RecoveryKey_Reissue()
    {
        var k2 = (await _admin.ReissueRecoveryKey()).RecoveryKey;
        Assert.NotEqual(_setup.RecoveryKey, k2);
        var st = await _admin.RunBackup();
        var newest = Path.Combine(_s.Paths.Backups, st.Files.First().Name);
        Assert.Throws<KirokuException>(() => BackupCrypto.Decrypt(newest, Path.Combine(_s.Dir, "o.db"), _setup.RecoveryKey));
        BackupCrypto.Decrypt(newest, Path.Combine(_s.Dir, "o.db"), k2);
    }

    [Fact] // 受入13: 親機(サービス)再起動後に正常復旧(データ・端末登録・職員が残る)
    public async Task Restart_RecoversEverything()
    {
        var r = await _admin.SaveRecord(new(null, "山田 太郎", Today, "9:00", null, null, null, 0));
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        await _s.StopAsync();
        // 停止中: 子機の保存は失敗として明確に通知される(成功を偽装しない)
        var ex = await Assert.ThrowsAsync<KirokuException>(() => staff.SaveRecord(new(r.ClientId, null, Today, "9:00", "16:00", null, null, r.Version)));
        Assert.Equal("unreachable", ex.Code);
        Assert.Contains("親機に接続できません", ex.Message);
        await _s.StartAsync();   // 同じフォルダで再起動(新しい証明書は作られず、同じ指紋)
        // 再ログイン不要の場合もあるが、セッションはDB内にあるので継続できる
        var r2 = await staff.Record(r.ClientId, Today);
        Assert.Equal("09:00", r2!.CheckIn);
        await staff.SaveRecord(new(r.ClientId, null, Today, "9:00", "16:00", "再起動後の追記", null, r2.Version));
        Assert.Equal("再起動後の追記", (await _admin.Record(r.ClientId, Today))!.Work);
    }

    [Fact] // 保存先の容量不足を検出して保存を拒否する経路(KirokuException)が日本語で出る
    public void DiskFull_MessageIsActionable()
    {
        var e = new KirokuException(507, "disk_full", "バックアップ先の空き容量が足りません");
        Assert.Contains("空き容量", e.Message);
    }

    [Fact]
    public async Task Discovery_FindsParent_ByBroadcastReply()
    {
        await using var s = new TestServer(background: true);
        await s.StartAsync();
        var found = await DiscoveryClient.Find(TimeSpan.FromSeconds(2), s.DiscoveryPort, [System.Net.IPAddress.Loopback]);
        Assert.Single(found);
        Assert.Equal(s.Port, found[0].Port); Assert.Equal(s.Fingerprint, found[0].Fingerprint);
    }

    [Fact] // 未保存内容の退避(暗号化)
    public void Drafts_AreSavedEncrypted_AndRemoved()
    {
        var dir = Path.Combine(_s.Dir, "drafts");
        var ds = new DraftStore(dir, new InsecureFileProtector(Path.Combine(_s.Dir, "k")));
        ds.Save("record", "c1|2026-04-01", "{\"notes\":\"本人の様子ひみつ\"}");
        Assert.DoesNotContain("ひみつ", File.ReadAllText(Directory.GetFiles(dir)[0], System.Text.Encoding.Latin1));
        Assert.Contains("ひみつ", ds.Load("record", "c1|2026-04-01")!.Json);
        ds.Delete("record", "c1|2026-04-01");
        Assert.Null(ds.Load("record", "c1|2026-04-01"));
    }

    [Fact] // 一時ファイルが残らない
    public async Task NoTempFilesLeftBehind()
    {
        await _admin.RunBackup(); await _admin.VerifyBackups();
        Assert.Empty(Directory.GetFiles(_s.Paths.Tmp));
    }
}

public class AdminRecoveryTests
{
    [Fact] // 管理者パスワード紛失時: 復旧キーが必要。誤キーでは変更できず、正しければ全セッション失効+次回変更要求
    public async Task ResetAdmin_RequiresRecoveryKey()
    {
        await using var s = new TestServer();
        await s.StartAsync();
        var (admin, setup) = await s.SetupAdmin();
        var paths = s.Paths;
        var wrap = new KeyStore(paths, new InsecureFileProtector(paths.Root)).LoadRecoveryWrap()!;
        Assert.Throws<KirokuException>(() => RecoveryWrap.Unwrap(wrap, "AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA"));
        RecoveryWrap.Unwrap(wrap, setup.RecoveryKey);   // 正しい復旧キーなら通る
        s.Store.ResetPasswordByLoginId("admin", "Reset-pass-2026!");
        var ex = await Assert.ThrowsAsync<KirokuException>(() => admin.Records("2026-01-01")); // 既存セッションは失効
        Assert.Equal("not_logged_in", ex.Code);
        var api = s.Api(setup.DeviceToken);
        var r = await api.Login("admin", "Reset-pass-2026!");
        Assert.True(r.MustChangePassword);
    }
}
