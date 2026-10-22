using System.Windows;
using System.Windows.Controls;
using Kiroku.Client;
using Kiroku.Core;

namespace Kiroku.App;

/// <summary>起動から終了までの流れ: 親機/子機の判定 → 初期設定または接続 → ログイン → 日々の記録。</summary>
internal static class Flow
{
    public static async Task Run()
    {
        var st = new AppState { };
        st.Settings = st.Local.Load();

        if (st.Settings.Mode == "")
        {
            var hint = File.Exists(AppState.ModeHintFile) ? File.ReadAllText(AppState.ModeHintFile).Trim() : "";
            if (hint is "parent" or "child") st.Settings.Mode = hint;
            else
            {
                var pick = Ui.Choose(null, "最初の設定", "このパソコンの役割を選んでください。\n(親機は事業所に1台だけです)", "このパソコンを親機にする", "このパソコンを子機にする");
                if (pick < 0) return;
                st.Settings.Mode = pick == 0 ? "parent" : "child";
            }
            st.SaveSettings();
        }

        if (!(st.IsParent ? await BootstrapParent(st) : await BootstrapChild(st))) return;

        while (true)
        {
            var login = new LoginWindow(st);
            login.ShowDialog();
            if (login.Result == null) return;
            st.Staff = login.Result;
            var main = new MainWindow(st);
            main.ShowDialog();
            try { await st.Api.Logout(); } catch { /* ログアウト失敗でも画面は戻す */ }
            if (!main.LoggedOut) return;
        }
    }

    private static async Task<bool> BootstrapParent(AppState st)
    {
        var wait = new Window { SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.ToolWindow, Content = Ui.Text("共有サービスを起動しています…\nしばらくお待ちください。", 18) };
        ((TextBlock)wait.Content).Margin = new Thickness(30);
        Ui.Style(wait, "事業所記録アプリ", double.NaN, double.NaN); wait.SizeToContent = SizeToContent.WidthAndHeight;
        wait.Show();
        HelloInfo? hello = null; KirokuException? last = null;
        for (var i = 0; i < 40 && hello == null; i++)
        {
            try { hello = await Pairing.HelloLocal(st.Settings.Port); }
            catch (KirokuException e) { last = e; await Task.Delay(1000); }
        }
        wait.Close();
        if (hello == null)
        {
            Ui.Error(null, (last?.Message ?? "共有サービスに接続できません。") + "\n\nパソコンを再起動してもつながらない場合は、管理者に連絡してください。");
            return false;
        }

        if (!hello.Initialized)
        {
            var setup = new SetupParentWindow(st);
            setup.ShowDialog();
            if (setup.Result == null) return false;
            st.Settings.Host = "127.0.0.1"; st.Settings.Fingerprint = setup.Result.Fingerprint;
            st.Local.SetToken(st.Settings, setup.Result.DeviceToken);
            st.SaveSettings();
            new RecoveryKeyWindow(setup.Result.RecoveryKey).ShowDialog();
        }
        else if (!st.Settings.IsConfigured || !string.Equals(st.Settings.Fingerprint, hello.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            Ui.Error(null, "この親機の端末登録情報が見つかりません(再インストールなどで失われた可能性があります)。\n\n管理者の手順書「親機の端末登録のやり直し」に従ってください。");
            return false;
        }
        st.Api = st.Local.CreateApi(st.Settings)!;
        return true;
    }

    private static async Task<bool> BootstrapChild(AppState st)
    {
        if (!st.Settings.IsConfigured)
        {
            var w = new ConnectChildWindow(st);
            w.ShowDialog();
            if (!w.Connected) return false;
        }
        while (true)
        {
            st.Api = st.Local.CreateApi(st.Settings)!;
            try { await st.Api.Hello(); return true; }
            catch (KirokuException e)
            {
                var pick = Ui.Choose(null, "親機に接続できません", e.Message + "\n\n親機が起動していること、同じWi-Fiにつながっていることを確認してください。", "もう一度試す", "接続設定をやり直す", "終了");
                if (pick == 0) continue;
                if (pick == 1)
                {
                    st.Settings.ProtectedDeviceToken = null; st.Settings.Fingerprint = ""; st.SaveSettings();
                    var w = new ConnectChildWindow(st); w.ShowDialog();
                    if (!w.Connected) return false;
                    continue;
                }
                return false;
            }
        }
    }
}
