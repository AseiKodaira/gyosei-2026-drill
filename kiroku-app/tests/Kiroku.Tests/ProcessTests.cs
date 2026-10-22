using System.Diagnostics;
using Kiroku.Client;
using Kiroku.Core;

namespace Kiroku.Tests;

/// <summary>実際のサーバープロセスを起動し、強制終了(電源断相当)→再起動してもデータが残ることを確認する。</summary>
public class ProcessTests
{
    private static Process Start(string dir, int port, int dport)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Kiroku.Server.dll");
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" run") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["KIROKU_DATA"] = dir; psi.Environment["KIROKU_PORT"] = port.ToString(); psi.Environment["KIROKU_DISCOVERY_PORT"] = dport.ToString();
        psi.Environment["KIROKU_BIND_LOOPBACK"] = "1"; psi.Environment["KIROKU_ALLOW_INSECURE_KEYSTORE"] = "1";
        return Process.Start(psi)!;
    }

    private static async Task<HelloInfo> WaitHello(int port)
    {
        for (var i = 0; i < 100; i++)
        {
            try { return await Pairing.HelloLocal(port); } catch (KirokuException) { await Task.Delay(200); }
        }
        throw new TimeoutException("サーバーが起動しませんでした");
    }

    [Fact] // 受入13: 強制終了後の再起動で、記録・端末登録・職員・終礼メモが残り、そのまま使える
    public async Task KillNine_Restart_DataSurvives()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiroku-proc-" + Guid.NewGuid().ToString("N"));
        var port = Net.FreeTcpPort(); var dport = Net.FreeUdpPort();
        var p1 = Start(dir, port, dport);
        try
        {
            var hello = await WaitHello(port);
            Assert.False(hello.Initialized);
            var setup = await Pairing.SetupParent(port, new SetupRequest("admin", "管理者", "Admin-pass-2026!", "親機"));
            using var api = new KirokuApi("127.0.0.1", port, setup.Fingerprint, setup.DeviceToken);
            await api.Login("admin", "Admin-pass-2026!");
            var r = await api.SaveRecord(new(null, "強制終了 太郎", "2026-05-01", "9:00", null, "検品", "", 0));
            await api.SaveMemo(new("2026-05-01", "終礼メモ", 0));
            p1.Kill(entireProcessTree: true); await p1.WaitForExitAsync();   // kill -9 相当

            var p2 = Start(dir, port, dport);
            try
            {
                var hello2 = await WaitHello(port);
                Assert.True(hello2.Initialized);
                Assert.Equal(setup.Fingerprint, hello2.Fingerprint);            // 証明書は変わらない(子機の再登録は不要)
                using var api2 = new KirokuApi("127.0.0.1", port, setup.Fingerprint, setup.DeviceToken);
                await api2.Login("admin", "Admin-pass-2026!");                  // 職員・端末登録が残っている
                var again = await api2.Record(r.ClientId, "2026-05-01");
                Assert.Equal("検品", again!.Work);
                Assert.Equal("終礼メモ", (await api2.Memo("2026-05-01")).Text);
                Assert.True((await api2.SystemStatus()).DbOk);
                Assert.True((await api2.SystemStatus()).AuditChainOk);
            }
            finally { if (!p2.HasExited) p2.Kill(true); }
        }
        finally { if (!p1.HasExited) p1.Kill(true); try { Directory.Delete(dir, true); } catch { } }
    }
}
