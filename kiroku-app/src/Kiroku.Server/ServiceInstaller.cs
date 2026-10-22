using System.Diagnostics;
using Kiroku.Core;

namespace Kiroku.Server;

/// <summary>Windowsサービス登録・ファイアウォール(同一サブネット限定)・データフォルダ権限を自動設定する。管理者権限で実行される想定。</summary>
public static class ServiceInstaller
{
    public static int Install(DataPaths paths)
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Windows専用です。"); return 2; }
        var exe = Environment.ProcessPath!;
        paths.Ensure();
        Run("icacls", $"\"{paths.Root}\" /inheritance:r /grant:r \"SYSTEM:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\"");
        Run("sc.exe", "stop KirokuServer", ignore: true);
        Run("sc.exe", "delete KirokuServer", ignore: true);
        var ok = Run("sc.exe", $"create KirokuServer binPath= \"\\\"{exe}\\\" run\" start= delayed-auto DisplayName= \"事業所記録アプリ 共有サービス\"");
        Run("sc.exe", "description KirokuServer \"事業所内のパソコン間で日々の記録を共有します(インターネットには接続しません)\"");
        Run("sc.exe", "failure KirokuServer reset= 86400 actions= restart/5000/restart/5000/restart/30000");
        Run("netsh", "advfirewall firewall delete rule name=\"Kiroku HTTPS\"", ignore: true);
        Run("netsh", "advfirewall firewall delete rule name=\"Kiroku Discovery\"", ignore: true);
        Run("netsh", $"advfirewall firewall add rule name=\"Kiroku HTTPS\" dir=in action=allow protocol=TCP localport={Ports.Https} remoteip=LocalSubnet profile=any program=\"{exe}\"");
        Run("netsh", $"advfirewall firewall add rule name=\"Kiroku Discovery\" dir=in action=allow protocol=UDP localport={Ports.Discovery} remoteip=LocalSubnet profile=any program=\"{exe}\"");
        ok &= Run("sc.exe", "start KirokuServer");
        Console.WriteLine(ok ? "共有サービスを登録して開始しました。" : "共有サービスの登録に失敗しました。管理者として実行しているか確認してください。");
        return ok ? 0 : 1;
    }

    public static int Uninstall()
    {
        Run("sc.exe", "stop KirokuServer", ignore: true);
        Run("sc.exe", "delete KirokuServer", ignore: true);
        Run("netsh", "advfirewall firewall delete rule name=\"Kiroku HTTPS\"", ignore: true);
        Run("netsh", "advfirewall firewall delete rule name=\"Kiroku Discovery\"", ignore: true);
        Console.WriteLine("共有サービスを削除しました(記録データは残っています)。");
        return 0;
    }

    private static bool Run(string file, string args, bool ignore = false)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(60_000);
            return ignore || p.ExitCode == 0;
        }
        catch { return ignore; }
    }
}
