using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Kiroku.App;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        var ja = new CultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentCulture = ja; CultureInfo.DefaultThreadCurrentUICulture = ja;
        Thread.CurrentThread.CurrentCulture = ja; Thread.CurrentThread.CurrentUICulture = ja;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ja-JP")));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            // 内容(個人情報を含みうる)は画面にもログにも出さない
            MessageBox.Show("想定外のエラーが起きました。アプリを閉じて開き直してください。\n未保存の入力は一時的に退避されている場合があります。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await Flow.Run(); }
            finally { app.Shutdown(); }
        });
        app.Run();
    }
}
