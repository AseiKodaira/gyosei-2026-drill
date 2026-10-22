using System.Net;
using System.Net.Sockets;
using Kiroku.Client;
using Kiroku.Core;
using Kiroku.Server;

namespace Kiroku.Tests;

public static class Net
{
    public static int FreeTcpPort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    public static int FreeUdpPort() { using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)u.Client.LocalEndPoint!).Port; }
}

/// <summary>本物のKestrel+TLS+暗号化SQLiteの親機を一時フォルダに起動する。</summary>
public sealed class TestServer : IAsyncDisposable
{
    public DataPaths Paths { get; }
    public int Port { get; }
    public int DiscoveryPort { get; }
    public WebApplication App { get; private set; } = null!;
    public string Fingerprint { get; private set; } = "";
    public string Dir { get; }
    private readonly bool _background;

    public TestServer(string? dir = null, bool background = false)
    {
        Dir = dir ?? Path.Combine(Path.GetTempPath(), "kiroku-test-" + Guid.NewGuid().ToString("N"));
        Paths = new DataPaths(Path.Combine(Dir, "data"));
        Port = Net.FreeTcpPort(); DiscoveryPort = Net.FreeUdpPort(); _background = background;
    }

    public async Task StartAsync()
    {
        App = ServerHost.Build(new ServerOptions(Paths, new InsecureFileProtector(Paths.Root), Port, DiscoveryPort, BindAny: false, Background: _background), []);
        await App.StartAsync();
        using var probe = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => { Fingerprint = Hashing.Sha256Hex(c!.RawData); return true; } });
        await probe.GetAsync($"https://127.0.0.1:{Port}/api/hello");
    }

    public async Task StopAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    public KirokuStore Store => App.Services.GetRequiredService<KirokuStore>();
    public BackupService Backups => App.Services.GetRequiredService<BackupService>();
    public KirokuApi Api(string? deviceToken, string host = "127.0.0.1") => new(host, Port, Fingerprint, deviceToken);

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { }
        try { Directory.Delete(Dir, true); } catch { }
    }

    public const string AdminPw = "Admin-pass-2026!";

    /// <summary>初期設定を行い、親機端末+管理者ログイン済みAPIを返す。</summary>
    public async Task<(KirokuApi Api, SetupResult Setup)> SetupAdmin()
    {
        var setup = await Pairing.SetupParent(Port, new SetupRequest("admin", "管理者 花子", AdminPw, "親機"));
        var api = Api(setup.DeviceToken);
        await api.Login("admin", AdminPw);
        return (api, setup);
    }

    /// <summary>子機を接続コード+管理者承認で登録し、そのAPI(未ログイン)を返す。</summary>
    public async Task<KirokuApi> PairChild(KirokuApi admin, string name = "子機")
    {
        var code = (await admin.NewPairingCode()).Code;
        var pair = Task.Run(() => Pairing.PairChild("127.0.0.1", Port, code, name));
        string? id = null;
        for (var i = 0; i < 40 && id == null; i++)
        {
            await Task.Delay(100);
            id = (await admin.Devices()).FirstOrDefault(d => d.Status == "pending")?.Id;
        }
        await admin.ApproveDevice(id!);
        var r = await pair;
        Assert.Equal(Fingerprint, r.Fingerprint);
        return Api(r.DeviceToken);
    }

    public async Task<KirokuApi> NewStaffApi(KirokuApi admin, string loginId, string name, bool canPrint = false, string pw = "Staff-pass-2026!")
    {
        await admin.CreateStaff(new StaffCreateRequest(loginId, name, pw, canPrint));
        var dev = await PairChild(admin, "端末-" + loginId);
        var r = await dev.Login(loginId, pw);
        Assert.True(r.MustChangePassword); // 管理者が決めた初期パスワードは初回変更を促す
        await dev.ChangePassword(pw, pw + "x");
        return dev;
    }
}
