using System.Windows;
using System.Windows.Controls;
using Kiroku.Client;
using Kiroku.Core;
using Microsoft.Win32;

namespace Kiroku.App;

/// <summary>親機の初回設定: 管理者を登録する(ここで初めて管理者パスワードを決める)。</summary>
internal sealed class SetupParentWindow : AppWindow
{
    public SetupResult? Result { get; private set; }
    private readonly TextBox _name = Ui.Box(), _id = Ui.Box();
    private readonly PasswordBox _pw = new() { FontSize = 17, MinHeight = 38 }, _pw2 = new() { FontSize = 17, MinHeight = 38 };
    private readonly TextBlock _msg = Ui.Text("", 14, false, Ui.Bad);

    public SetupParentWindow(AppState st)
    {
        Ui.Style(this, "親機の初期設定", 560, 640);
        var sp = new StackPanel { Margin = new Thickness(24) };
        sp.Children.Add(Ui.Text("親機の初期設定", 24, true));
        sp.Children.Add(Ui.Text("このパソコンを、事業所の記録を保管する「親機」にします。最初に管理者(責任者)を1人登録してください。", 15, false, Ui.Muted));
        sp.Children.Add(new Border { Height = 12 });
        _name.Text = ""; _id.Text = "admin";
        sp.Children.Add(Ui.Field("管理者のお名前", _name));
        sp.Children.Add(Ui.Field("ログインID(半角英数字)", _id));
        sp.Children.Add(Ui.Field("パスワード(10文字以上)", _pw, "他の人が推測できないもの。紙に書く場合は施錠できる場所に保管してください。"));
        sp.Children.Add(Ui.Field("パスワード(確認のためもう一度)", _pw2));
        sp.Children.Add(_msg);
        sp.Children.Add(Ui.Btn("設定する", () => Safe(() => Go(st)), primary: true, minWidth: 200));
        Content = sp;
        Loaded += (_, _) => _name.Focus();
    }

    private async Task Go(AppState st)
    {
        _msg.Text = "";
        if (_pw.Password != _pw2.Password) { _msg.Text = "パスワードが一致しません。もう一度入力してください。"; return; }
        var err = PasswordHasher.Validate(_pw.Password);
        if (err != null) { _msg.Text = err; return; }
        _msg.Text = "設定しています…"; _msg.Foreground = Ui.Muted;
        try
        {
            Result = await Pairing.SetupParent(st.Settings.Port, new SetupRequest(_id.Text, _name.Text, _pw.Password, "親機"));
            Close();
        }
        catch (KirokuException e) { _msg.Foreground = Ui.Bad; _msg.Text = e.Message; }
    }
}

/// <summary>復旧キーの表示。紙またはUSBメモリに控えたことを確認するまで閉じられない。</summary>
internal sealed class RecoveryKeyWindow : Window
{
    private bool _ok;
    public RecoveryKeyWindow(string key)
    {
        Ui.Style(this, "復旧キー(必ず保管してください)", 640, 560);
        var sp = new StackPanel { Margin = new Thickness(24) };
        sp.Children.Add(Ui.Text("復旧キー", 24, true));
        sp.Children.Add(Ui.Text("親機が故障したときにバックアップから記録を取り戻すための「合鍵」です。この画面は今回しか表示されません。", 15));
        var k = new TextBox { Text = key, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 24, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14), Padding = new Thickness(10), BorderBrush = Ui.Primary };
        sp.Children.Add(k);
        sp.Children.Add(Ui.Text("・紙に印刷して鍵のかかる場所に保管  ・USBメモリに保存して別の場所に保管\n・このパソコンの中だけに置かないでください(故障すると一緒に失われます)\n・なくすとバックアップが開けなくなります。誰にも見せないでください。", 14, false, Ui.Warn));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Ui.Btn("印刷する", () => Print(key)));
        row.Children.Add(Ui.Btn("ファイルに保存(USBメモリ等)", () => SaveFile(key), minWidth: 240));
        sp.Children.Add(row);
        var chk = new CheckBox { Content = "復旧キーを紙またはUSBメモリに保管しました", FontSize = 16, Margin = new Thickness(0, 14, 0, 6) };
        var done = Ui.Btn("完了", () => { _ok = true; Close(); }, primary: true, minWidth: 160);
        done.IsEnabled = false;
        chk.Checked += (_, _) => done.IsEnabled = true; chk.Unchecked += (_, _) => done.IsEnabled = false;
        sp.Children.Add(chk); sp.Children.Add(done);
        Content = sp;
        Closing += (_, e) => { if (!_ok) { e.Cancel = true; Ui.Info(this, "復旧キーを保管したことにチェックを入れてから「完了」を押してください。"); } };
    }

    private void Print(string key)
    {
        var pd = new PrintDialog();
        if (pd.ShowDialog() != true) return;
        var doc = new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("事業所記録アプリ 復旧キー\n\n" + key + "\n\n親機が故障したときにバックアップを開くための鍵です。鍵のかかる場所に保管してください。")) { FontSize = 20 }) { PagePadding = new Thickness(60) };
        pd.PrintDocument(((System.Windows.Documents.IDocumentPaginatorSource)doc).DocumentPaginator, "復旧キー");
    }

    private void SaveFile(string key)
    {
        var d = new SaveFileDialog { FileName = "復旧キー.txt", Filter = "テキスト|*.txt" };
        if (d.ShowDialog(this) == true) File.WriteAllText(d.FileName, "事業所記録アプリ 復旧キー\r\n" + key + "\r\n");
    }
}

/// <summary>子機の接続設定: 親機を自動検出し、接続コードで登録を申請する(承認は親機側)。</summary>
internal sealed class ConnectChildWindow : AppWindow
{
    public bool Connected { get; private set; }
    private readonly ListBox _found = new() { MinHeight = 90, FontSize = 16 };
    private readonly TextBox _host = Ui.Box(), _code = Ui.Box(), _dev = Ui.Box();
    private readonly TextBlock _msg = Ui.Text("", 14, false, Ui.Muted);
    private List<FoundServer> _servers = [];
    private readonly AppState _st;

    public ConnectChildWindow(AppState st)
    {
        _st = st;
        Ui.Style(this, "子機の接続設定", 620, 760);
        var sp = new StackPanel { Margin = new Thickness(24) };
        sp.Children.Add(Ui.Text("親機につなぐ", 24, true));
        sp.Children.Add(Ui.Text("同じWi-Fiにつながっている親機を探します。見つかったら選び、親機の管理画面に表示される「接続コード」を入力してください。", 15, false, Ui.Muted));
        sp.Children.Add(Ui.Field("見つかった親機", _found));
        sp.Children.Add(Ui.Btn("もう一度探す", () => Safe(Search), minWidth: 160));
        sp.Children.Add(Ui.Field("見つからないとき: 親機のアドレス(親機の管理画面に表示)", _host));
        sp.Children.Add(Ui.Btn("つながるか調べる", () => Safe(Diagnose), minWidth: 160));
        sp.Children.Add(Ui.Field("接続コード(8桁の数字)", _code));
        _dev.Text = st.Settings.DeviceName;
        sp.Children.Add(Ui.Field("この端末の名前(親機の一覧に表示されます)", _dev));
        sp.Children.Add(_msg);
        sp.Children.Add(Ui.Btn("登録を申請する", () => Safe(Pair), primary: true, minWidth: 220));
        Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => Safe(Search);
    }

    private async Task Search()
    {
        _msg.Foreground = Ui.Muted; _msg.Text = "親機を探しています…";
        _servers = await DiscoveryClient.Find();
        _found.ItemsSource = _servers.Select(s => $"{s.Name}  ({s.Host})").ToList();
        if (_servers.Count == 1) _found.SelectedIndex = 0;
        _msg.Text = _servers.Count == 0 ? await DiscoveryClient.Diagnose(null) : $"{_servers.Count}台の親機が見つかりました。";
    }

    private async Task Diagnose() => _msg.Text = await DiscoveryClient.Diagnose(_host.Text.Trim(), _st.Settings.Port);

    private async Task Pair()
    {
        var sel = _found.SelectedIndex >= 0 && _found.SelectedIndex < _servers.Count ? _servers[_found.SelectedIndex] : null;
        var host = sel?.Host ?? _host.Text.Trim();
        var port = sel?.Port ?? _st.Settings.Port;
        if (host == "") { _msg.Foreground = Ui.Bad; _msg.Text = "親機を選ぶか、アドレスを入力してください。"; return; }
        if (_code.Text.Trim().Length != 8) { _msg.Foreground = Ui.Bad; _msg.Text = "接続コードは8桁の数字です。親機の管理画面で確認してください。"; return; }
        _msg.Foreground = Ui.Muted; _msg.Text = "親機に申請しています…";
        try
        {
            var r = await Pairing.PairChild(host, port, _code.Text.Trim(), _dev.Text.Trim(), m => _msg.Text = m);
            if (sel != null && !string.Equals(sel.Fingerprint, r.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new KirokuException(400, "mitm", "探して見つかった親機と、実際につながった相手が一致しません。接続を中止しました。");
            _st.Settings.Host = host; _st.Settings.Port = port; _st.Settings.Fingerprint = r.Fingerprint;
            _st.Settings.DeviceName = _dev.Text.Trim(); _st.Settings.Mode = "child";
            _st.Local.SetToken(_st.Settings, r.DeviceToken);
            _st.SaveSettings();
            Connected = true; Close();
        }
        catch (KirokuException e) { _msg.Foreground = Ui.Bad; _msg.Text = e.Message; }
    }
}

/// <summary>職員個人のログイン。共有アカウントは使わない。</summary>
internal sealed class LoginWindow : AppWindow
{
    public StaffInfo? Result { get; private set; }
    private readonly TextBox _id = Ui.Box();
    private readonly PasswordBox _pw = new() { FontSize = 17, MinHeight = 38, Padding = new Thickness(6, 4, 6, 4) };
    private readonly TextBlock _msg = Ui.Text("", 14, false, Ui.Bad);
    private readonly AppState _st;

    public LoginWindow(AppState st)
    {
        _st = st;
        Ui.Style(this, "ログイン", 480, 460);
        var sp = new StackPanel { Margin = new Thickness(28) };
        sp.Children.Add(Ui.Text("事業所記録アプリ", 26, true));
        sp.Children.Add(Ui.Text("職員のIDとパスワードでログインしてください。", 15, false, Ui.Muted));
        sp.Children.Add(new Border { Height = 14 });
        sp.Children.Add(Ui.Field("ログインID", _id));
        sp.Children.Add(Ui.Field("パスワード", _pw));
        sp.Children.Add(_msg);
        sp.Children.Add(Ui.Btn("ログイン", () => Safe(Go), primary: true, minWidth: 200));
        Content = sp;
        _pw.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Safe(Go); };
        _id.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) _pw.Focus(); };
        Loaded += (_, _) => _id.Focus();
    }

    private async Task Go()
    {
        _msg.Text = "";
        try
        {
            var r = await _st.Api.Login(_id.Text.Trim(), _pw.Password);
            var staff = r.Staff;
            var must = r.MustChangePassword;
            var current = _pw.Password;
            while (must)
            {
                var v = Ui.Prompt(this, "パスワードの変更", "初めてのログインです。ご自身だけが知っている新しいパスワード(10文字以上)に変えてください。",
                    ("新しいパスワード", true, ""), ("新しいパスワード(確認)", true, ""));
                if (v == null) { await _st.Api.Logout(); return; }
                if (v[0] != v[1]) { Ui.Error(this, "2回の入力が一致しません。"); continue; }
                try { await _st.Api.ChangePassword(current, v[0]); must = false; }
                catch (KirokuException e) { Ui.Error(this, e.Message); }
            }
            Result = staff; Close();
        }
        catch (KirokuException e) when (e.Code == "device_unregistered")
        {
            _msg.Text = e.Message;
            if (!_st.IsParent && Ui.Ask(this, "この端末の登録をやり直しますか?(親機での承認が必要です)"))
            {
                _st.Settings.ProtectedDeviceToken = null; _st.Settings.Fingerprint = ""; _st.SaveSettings();
                var w = new ConnectChildWindow(_st); w.ShowDialog();
                if (w.Connected) { _st.Api = _st.Local.CreateApi(_st.Settings)!; _msg.Text = "登録しました。ログインしてください。"; }
            }
        }
        catch (KirokuException e) { _msg.Text = e.Message; _pw.Clear(); _pw.Focus(); }
    }
}
