using Kiroku.Core;
using Kiroku.Server;

// 使い方: Kiroku.Server.exe [run | install-service | uninstall-service | restore <ファイル> | reset-admin <ログインID>]
var cmd = args.Length > 0 ? args[0] : "run";
var paths = DataPaths.Default();
switch (cmd)
{
    case "install-service": return ServiceInstaller.Install(paths);
    case "uninstall-service": return ServiceInstaller.Uninstall();
    case "reset-admin": return RestoreCli.ResetAdmin(paths, args.Skip(1).ToArray());
    case "restore": return RestoreCli.Run(paths, args.Skip(1).ToArray());
    default:
        // 試験用: ポートと待受範囲を環境変数で変更できる(通常運用では未設定)
        var port = int.TryParse(Environment.GetEnvironmentVariable("KIROKU_PORT"), out var p) ? p : Ports.Https;
        var dport = int.TryParse(Environment.GetEnvironmentVariable("KIROKU_DISCOVERY_PORT"), out var dp) ? dp : Ports.Discovery;
        var any = Environment.GetEnvironmentVariable("KIROKU_BIND_LOOPBACK") != "1";
        var app = ServerHost.Build(new ServerOptions(paths, KeyProtectors.ForServer(paths.Root), port, dport, any), args.Skip(cmd == "run" ? 1 : 0).ToArray());
        await app.RunAsync();
        return 0;
}
