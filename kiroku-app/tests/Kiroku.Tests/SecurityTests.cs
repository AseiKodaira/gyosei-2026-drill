using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Text;
using Kiroku.Client;
using Kiroku.Core;
using Microsoft.Data.Sqlite;

namespace Kiroku.Tests;

public class SecurityTests : IAsyncLifetime
{
    private readonly TestServer _s = new();
    private KirokuApi _admin = null!;
    public async Task InitializeAsync() { await _s.StartAsync(); (_admin, _) = await _s.SetupAdmin(); }
    public async Task DisposeAsync() => await _s.DisposeAsync();
    private static string Today => DateTime.Today.ToString("yyyy-MM-dd");

    [Fact] // 受入11: 未登録端末は記録を取得・変更できない
    public async Task UnregisteredDevice_IsRejected_ForEverything()
    {
        await _admin.SaveRecord(new(null, "山田 太郎", Today, "9:00", null, null, null, 0));
        var rogue = _s.Api(null);
        foreach (var call in new Func<Task>[] { () => rogue.Records(Today), () => rogue.Login("admin", TestServer.AdminPw), () => rogue.Memo(Today) })
        {
            var ex = await Assert.ThrowsAsync<KirokuException>(call);
            Assert.Equal(401, ex.Status); Assert.Equal("device_unregistered", ex.Code);
        }
        var fake = _s.Api("0000000000000000000000000000000000000000000000000000000000000000");
        await Assert.ThrowsAsync<KirokuException>(() => fake.Records(Today));
    }

    [Fact] // 端末だけ登録済み・ログインなし、は拒否
    public async Task DeviceWithoutLogin_IsRejected()
    {
        var dev = await _s.PairChild(_admin);
        var ex = await Assert.ThrowsAsync<KirokuException>(() => dev.Records(Today));
        Assert.Equal("not_logged_in", ex.Code);
        dev.SessionToken = "deadbeef";
        await Assert.ThrowsAsync<KirokuException>(() => dev.Records(Today));
    }

    [Fact] // 通常職員は管理機能・他職員の操作ができない
    public async Task Staff_CannotUseAdminFeatures()
    {
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        var calls = new Func<Task>[]
        {
            () => staff.Staff(), () => staff.Devices(), () => staff.Audit(), () => staff.BackupStatus(), () => staff.RunBackup(),
            () => staff.NewPairingCode(), () => staff.SystemStatus(), () => staff.CreateStaff(new("x1234", "x", "Passw0rd-long!", false)),
            () => staff.SetCanPrint("anything", true), () => staff.Restore("local", "x.krbk", "AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA"),
            () => staff.ReissueRecoveryKey(), () => staff.SetBackupPath("/tmp"),
        };
        foreach (var c in calls)
        {
            var ex = await Assert.ThrowsAsync<KirokuException>(c);
            Assert.Equal(403, ex.Status);
        }
    }

    [Fact] // 退職者アカウントは直ちに無効(既存セッションも即失効)
    public async Task DisabledStaff_LosesAccessImmediately()
    {
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        await staff.Records(Today);
        var id = (await _admin.Staff()).First(x => x.LoginId == "sato").Id;
        await _admin.SetActive(id, false);
        var ex = await Assert.ThrowsAsync<KirokuException>(() => staff.Records(Today));
        Assert.Equal("not_logged_in", ex.Code);
        var ex2 = await Assert.ThrowsAsync<KirokuException>(() => staff.Login("sato", "Staff-pass-2026!x"));
        Assert.Equal("bad_login", ex2.Code);
        await _admin.SetActive(id, true);
        await staff.Login("sato", "Staff-pass-2026!x");
    }

    [Fact] // 端末の登録取消で、その端末は即遮断
    public async Task RevokedDevice_IsBlocked()
    {
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        var dev = (await _admin.Devices()).First(d => d.Name == "端末-sato");
        await _admin.RevokeDevice(dev.Id);
        var ex = await Assert.ThrowsAsync<KirokuException>(() => staff.Records(Today));
        Assert.Equal("device_unregistered", ex.Code);
    }

    [Fact] // 他端末のセッショントークンは使えない(端末に紐づく)
    public async Task SessionToken_IsBoundToDevice()
    {
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        var other = await _s.PairChild(_admin, "別端末");
        other.SessionToken = staff.SessionToken;
        var ex = await Assert.ThrowsAsync<KirokuException>(() => other.Records(Today));
        Assert.Equal("not_logged_in", ex.Code);
    }

    [Fact] // 連続失敗でロック
    public async Task Login_LocksAfterRepeatedFailures()
    {
        await _admin.CreateStaff(new("sato", "佐藤 職員", "Staff-pass-2026!", false));
        var dev = await _s.PairChild(_admin);
        for (var i = 0; i < 5; i++)
        {
            var e = await Assert.ThrowsAsync<KirokuException>(() => dev.Login("sato", "wrong-password-" + i));
            Assert.Equal("bad_login", e.Code);
        }
        var locked = await Assert.ThrowsAsync<KirokuException>(() => dev.Login("sato", "Staff-pass-2026!"));
        Assert.Equal("locked", locked.Code); // 正しいパスワードでもロック中は不可
        Assert.Contains((await _admin.Audit()), a => a.Action == "login_failed");
    }

    [Fact] // 初期パスワードのままでは記録にアクセスできない
    public async Task MustChangePassword_BlocksRecordAccess()
    {
        await _admin.CreateStaff(new("sato", "佐藤 職員", "Staff-pass-2026!", false));
        var dev = await _s.PairChild(_admin);
        var r = await dev.Login("sato", "Staff-pass-2026!");
        Assert.True(r.MustChangePassword);
        var ex = await Assert.ThrowsAsync<KirokuException>(() => dev.Records(Today));
        Assert.Equal("must_change_password", ex.Code);
        await dev.ChangePassword("Staff-pass-2026!", "New-Pass-2026!!");
        await dev.Records(Today);
    }

    [Fact] // 弱いパスワード・重複ログインIDを拒否
    public async Task WeakPassword_And_DuplicateLoginId_Rejected()
    {
        var weak = await Assert.ThrowsAsync<KirokuException>(() => _admin.CreateStaff(new("sato", "佐藤", "short", false)));
        Assert.Equal("weak_password", weak.Code);
        await _admin.CreateStaff(new("sato", "佐藤 職員", "Staff-pass-2026!", false));
        var dup = await Assert.ThrowsAsync<KirokuException>(() => _admin.CreateStaff(new("SATO", "別人", "Staff-pass-2026!", false)));
        Assert.Equal("login_exists", dup.Code);
    }

    [Fact] // 最後の管理者は無効化できない
    public async Task LastAdmin_CannotBeDisabled()
    {
        var me = (await _admin.Staff()).Single(x => x.IsAdmin);
        var ex = await Assert.ThrowsAsync<KirokuException>(() => _admin.SetActive(me.Id, false));
        Assert.Equal("last_admin", ex.Code);
    }

    [Fact] // 初期設定は二度とできない(乗っ取り防止)
    public async Task Setup_CannotBeRepeated()
    {
        var ex = await Assert.ThrowsAsync<KirokuException>(() => Pairing.SetupParent(_s.Port, new SetupRequest("evil", "x", "Evil-pass-2026!", "x")));
        Assert.Equal("already_setup", ex.Code);
    }

    [Fact] // 受入: DBファイルは暗号化されており、氏名等が平文で見えない
    public async Task DatabaseFile_IsEncrypted_AtRest()
    {
        await _admin.SaveRecord(new(null, "機密テスト太郎", Today, "9:00", "16:00", "特殊な作業内容XYZ", "服薬の様子ABC", 0));
        _s.Store.Read(c => { using var m = c.CreateCommand(); m.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; m.ExecuteNonQuery(); return 0; });
        var bytes = File.ReadAllBytes(_s.Paths.Db);
        Assert.False(Encoding.ASCII.GetString(bytes, 0, 15).StartsWith("SQLite format 3"));
        foreach (var needle in new[] { "機密テスト太郎", "特殊な作業内容XYZ", "服薬の様子ABC", "SQLite format" })
            Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(needle)), needle);
        foreach (var f in Directory.GetFiles(_s.Paths.Root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("INSECURE-DEV-KEY.bin")))
            Assert.False(Contains(File.ReadAllBytes(f), Encoding.UTF8.GetBytes("機密テスト太郎")), f);
        // 鍵なし/誤った鍵では開けない
        Assert.False(KirokuStore.VerifyFile(_s.Paths.Db, new byte[32]));
        // 鍵ファイルは保護された形で、生の鍵とは異なる
        Assert.NotEqual(_s.Store.DbKey, File.ReadAllBytes(_s.Paths.DbKey));
    }

    [Fact] // 平文の通信(HTTP)では受け付けない・証明書が違えば接続しない
    public async Task Transport_IsTlsOnly_AndPinned()
    {
        using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await Assert.ThrowsAnyAsync<Exception>(() => plain.GetAsync($"http://127.0.0.1:{_s.Port}/api/hello"));
        using var wrongPin = new KirokuApi("127.0.0.1", _s.Port, new string('a', 64), null);
        var ex = await Assert.ThrowsAsync<KirokuException>(() => wrongPin.Hello());
        Assert.Equal("unreachable", ex.Code); // 指紋が違う相手とは通信しない
    }

    [Fact] // 監査ログの連鎖: 正常→改ざん検知
    public async Task AuditLog_ChainDetectsTampering()
    {
        await _admin.SaveRecord(new(null, "山田 太郎", Today, "9:00", null, null, null, 0));
        Assert.True(_s.Store.VerifyAudit().Ok);
        // 鍵を持つ者が直接DBの監査行を書き換えた場合を想定
        _s.Store.Tx((c, t) => { using var m = c.CreateCommand(); m.Transaction = t; m.CommandText = "UPDATE audit SET detail='改ざん' WHERE seq=(SELECT min(seq)+1 FROM audit)"; m.ExecuteNonQuery(); return 0; });
        var (ok, bad) = _s.Store.VerifyAudit();
        Assert.False(ok); Assert.NotNull(bad);
        Assert.False((await _admin.SystemStatus()).AuditChainOk);
    }

    [Fact] // 監査ログに氏名・記録内容を含めない
    public async Task AuditLog_DoesNotContainPersonalContent()
    {
        await _admin.SaveRecord(new(null, "個人情報太郎", Today, "9:00", "16:00", "秘密の作業", "秘密の様子", 0));
        var all = string.Join("\n", (await _admin.Audit(2000)).Select(a => $"{a.Action}|{a.Target}|{a.Detail}"));
        Assert.DoesNotContain("個人情報太郎", all); Assert.DoesNotContain("秘密の", all);
        Assert.Contains("record_create", all);
    }

    [Fact]
    public async Task Pairing_WrongCode_Fails_AndCodeIsSingleUse()
    {
        var code = (await _admin.NewPairingCode()).Code;
        var wrong = await Assert.ThrowsAsync<KirokuException>(() => Pairing.PairChild("127.0.0.1", _s.Port, "00000000", "悪い端末", null, TimeSpan.FromSeconds(1)));
        Assert.Equal("bad_code", wrong.Code);
        // 正しいコードで申請 → 同じコードでの再申請は不可
        var t1 = Task.Run(() => Pairing.PairChild("127.0.0.1", _s.Port, code, "端末A", null, TimeSpan.FromSeconds(8)));
        await Task.Delay(1500);
        var reuse = await Assert.ThrowsAsync<KirokuException>(() => Pairing.PairChild("127.0.0.1", _s.Port, code, "端末B", null, TimeSpan.FromSeconds(1)));
        Assert.Contains(reuse.Code, new[] { "no_code", "bad_code" });
        var pending = (await _admin.Devices()).Where(d => d.Status == "pending").ToList();
        Assert.Single(pending); Assert.Equal("端末A", pending[0].Name);
        await _admin.RejectDevice(pending[0].Id);
        var rej = await Assert.ThrowsAsync<KirokuException>(() => t1);
        Assert.Equal("rejected", rej.Code);
    }

    [Fact] // 5回間違えるとコード自体が無効
    public async Task Pairing_BruteForce_InvalidatesCode()
    {
        var code = (await _admin.NewPairingCode()).Code;
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<KirokuException>(() => Pairing.PairChild("127.0.0.1", _s.Port, "1111111" + i, "x", null, TimeSpan.FromSeconds(1)));
        var ex = await Assert.ThrowsAsync<KirokuException>(() => Pairing.PairChild("127.0.0.1", _s.Port, code, "x", null, TimeSpan.FromSeconds(1)));
        Assert.Equal("no_code", ex.Code);
    }

    [Fact] // 親機になりすました中間者(別証明書)はコードの証明に答えられず検知される
    public async Task Pairing_DetectsImpostorServer()
    {
        var code = (await _admin.NewPairingCode()).Code;
        // 偽サーバー: 別の自己署名証明書で、本物のコード検証なしに「承認します」と答える
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=fake", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var tmp = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(tmp.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
        var port = Net.FreeTcpPort();
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, l => l.UseHttps(cert)));
        var fake = b.Build();
        fake.MapGet("/api/hello", () => "hi");
        fake.MapPost("/api/pairing/request", () => new PairingStartResult("id", "secret", "ffff"));
        await fake.StartAsync();
        try
        {
            var ex = await Assert.ThrowsAsync<KirokuException>(() => Pairing.PairChild("127.0.0.1", port, code, "端末", null, TimeSpan.FromSeconds(1)));
            Assert.Equal("mitm", ex.Code);
        }
        finally { await fake.StopAsync(); }
    }

    private static bool Contains(byte[] hay, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= hay.Length; i++)
        {
            var j = 0; while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    [Fact]
    public void Lan_AllowList()
    {
        Assert.True(Kiroku.Server.Lan.IsAllowed(IPAddress.Parse("192.168.1.20")));
        Assert.True(Kiroku.Server.Lan.IsAllowed(IPAddress.Parse("10.1.2.3")));
        Assert.True(Kiroku.Server.Lan.IsAllowed(IPAddress.Parse("172.20.0.5")));
        Assert.True(Kiroku.Server.Lan.IsAllowed(IPAddress.Parse("::ffff:192.168.0.9")));
        Assert.False(Kiroku.Server.Lan.IsAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.False(Kiroku.Server.Lan.IsAllowed(IPAddress.Parse("172.32.0.1")));
        Assert.False(Kiroku.Server.Lan.IsAllowed(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void PasswordHash_Works()
    {
        var h = PasswordHasher.Hash("Correct-Horse-9");
        Assert.True(PasswordHasher.Verify("Correct-Horse-9", h));
        Assert.False(PasswordHasher.Verify("Correct-Horse-8", h));
        Assert.DoesNotContain("Correct-Horse", h);
    }
}
