using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kiroku.Core;
using Microsoft.Win32;

namespace Kiroku.App;

/// <summary>管理者メニュー: 接続端末 / 職員アカウント / バックアップ / 操作履歴 / システム状態。</summary>
internal sealed class AdminWindow : AppWindow
{
    private readonly AppState _st;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    // 端末
    private readonly ListView _devs = new() { FontSize = 15 };
    private List<DeviceInfo> _devList = [];
    // 職員
    private readonly ListView _staff = new() { FontSize = 15 };
    private List<StaffInfo> _staffList = [];
    // バックアップ
    private readonly TextBlock _bkStatus = Ui.Text("", 15);
    private readonly TextBlock _bkWarn = Ui.Text("", 15, true, Ui.Bad);
    private readonly ListView _bkFiles = new() { FontSize = 15, MinHeight = 160 };
    private BackupStatus? _bk;
    // 履歴
    private readonly ListView _audit = new() { FontSize = 14 };
    private readonly TextBlock _auditState = Ui.Text("", 15, true);
    // システム
    private readonly TextBlock _sys = Ui.Text("", 16);

    private sealed record DevRow(string Name, string Status, string Requested, string LastSeen);
    private sealed record StaffRow(string Name, string LoginId, string Role, string Print, string Active);
    private sealed record FileRow(string Name, string Where, string Size, string At);
    private sealed record AuditRow(string At, string Staff, string Action, string Detail);

    private static readonly Dictionary<string, string> ActionNames = new()
    {
        ["login"] = "ログイン", ["logout"] = "ログアウト", ["login_failed"] = "ログイン失敗", ["login_blocked"] = "ログイン拒否(ロック中)",
        ["setup"] = "初期設定", ["record_create"] = "記録を新規作成", ["record_update"] = "記録を修正", ["record_history_view"] = "記録の修正履歴を閲覧",
        ["record_search"] = "記録を検索", ["client_create"] = "利用者を登録", ["client_rename"] = "利用者の氏名を修正",
        ["memo_create"] = "終礼メモを作成", ["memo_update"] = "終礼メモを更新", ["memo_history_view"] = "終礼メモの履歴を閲覧", ["print"] = "印刷・PDF出力",
        ["staff_create"] = "職員を追加", ["staff_password_reset"] = "職員のパスワード再設定", ["password_change"] = "パスワード変更",
        ["staff_disable"] = "職員を無効化", ["staff_enable"] = "職員を有効化", ["staff_print_permission"] = "印刷権限の変更",
        ["pairing_code_issue"] = "接続コードを表示", ["device_request"] = "端末の登録申請", ["device_approve"] = "端末を承認", ["device_reject"] = "端末を拒否", ["device_revoke"] = "端末の登録を取消",
        ["backup_ok"] = "バックアップ作成", ["backup_failed"] = "バックアップ失敗", ["backup_verify"] = "バックアップの確認", ["backup_config"] = "バックアップ先の設定",
        ["restore"] = "バックアップから復元", ["recovery_key_reissue"] = "復旧キーの再発行",
    };

    public AdminWindow(AppState st)
    {
        _st = st;
        Ui.Style(this, "管理", 1100, 760);
        var tabs = new TabControl { FontSize = 16, Margin = new Thickness(8) };
        tabs.Items.Add(new TabItem { Header = "接続端末", Content = DevicesTab() });
        tabs.Items.Add(new TabItem { Header = "職員アカウント", Content = StaffTab() });
        tabs.Items.Add(new TabItem { Header = "バックアップ", Content = BackupTab() });
        tabs.Items.Add(new TabItem { Header = "操作履歴", Content = AuditTab() });
        tabs.Items.Add(new TabItem { Header = "システム状態・更新", Content = SystemTab() });
        Content = tabs;
        Loaded += (_, _) => { Safe(RefreshAll); _timer.Start(); };
        _timer.Tick += (_, _) => Safe(RefreshDevices);
        Closed += (_, _) => _timer.Stop();
    }

    private async Task RefreshAll()
    {
        await RefreshDevices(); await RefreshStaff(); await RefreshBackup(); await RefreshAudit(); await RefreshSystem();
    }

    private static Grid Tab(UIElement list, params UIElement[] buttons)
    {
        var g = new Grid { Margin = new Thickness(10) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var wp = new WrapPanel();
        foreach (var b in buttons) wp.Children.Add(b);
        g.Children.Add(wp);
        Grid.SetRow(list, 1); g.Children.Add(list);
        return g;
    }

    private static void Cols(ListView lv, params (string H, double W, string P)[] cols)
    {
        var gv = new GridView();
        foreach (var (h, w, p) in cols) gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new System.Windows.Data.Binding(p) });
        lv.View = gv;
    }

    // ---------- 1. 接続端末 ----------
    private UIElement DevicesTab()
    {
        Cols(_devs, ("端末名", 260, nameof(DevRow.Name)), ("状態", 120, nameof(DevRow.Status)), ("申請日時", 200, nameof(DevRow.Requested)), ("最後の接続", 200, nameof(DevRow.LastSeen)));
        var info = Ui.Text("新しい子機をつなぐ手順: ①「接続コードを表示」→ ②子機で親機を選んでコードを入力 → ③ここに「承認待ち」が出たら「承認する」。", 14, false, Ui.Muted);
        var g = Tab(_devs,
            Ui.Btn("接続コードを表示", () => Safe(ShowCode), primary: true, minWidth: 180),
            Ui.Btn("承認する", () => Safe(() => DevAct(d => _st.Api.ApproveDevice(d.Id), "pending", "承認")), minWidth: 110),
            Ui.Btn("拒否する", () => Safe(() => DevAct(d => _st.Api.RejectDevice(d.Id), "pending", "拒否")), minWidth: 110),
            Ui.Btn("登録を取り消す", () => Safe(() => DevAct(d => _st.Api.RevokeDevice(d.Id), "approved", "取消", confirm: true)), minWidth: 150));
        var sp = new DockPanel(); DockPanel.SetDock(info, Dock.Top); sp.Children.Add(info); sp.Children.Add(g);
        return sp;
    }

    private async Task RefreshDevices()
    {
        var sel = _devs.SelectedIndex;
        _devList = await _st.Api.Devices();
        _devs.ItemsSource = _devList.Select(d => new DevRow(d.Name, d.Status switch { "pending" => "★承認待ち", "approved" => "使用中", "rejected" => "拒否", "revoked" => "取り消し済み", _ => d.Status },
            d.RequestedAt.Replace('T', ' '), d.LastSeen?.Replace('T', ' ') ?? "")).ToList();
        if (sel >= 0 && sel < _devList.Count) _devs.SelectedIndex = sel;
    }

    private async Task ShowCode()
    {
        var c = await _st.Api.NewPairingCode();
        var ips = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString());
        Ui.Info(this, $"接続コード:  {c.Code}\n\n有効期限 {c.ExpiresAt} まで(1回だけ使えます)\n\n子機で親機が自動で見つからないときは、次のアドレスを入力してください:\n{string.Join("\n", ips)}", "接続コード");
    }

    private async Task DevAct(Func<DeviceInfo, Task> act, string needStatus, string verb, bool confirm = false)
    {
        var i = _devs.SelectedIndex;
        if (i < 0) { Ui.Info(this, "一覧から端末を選んでください。"); return; }
        var d = _devList[i];
        if (d.Status != needStatus) { Ui.Info(this, $"この端末は{verb}できる状態ではありません。"); return; }
        if (confirm && !Ui.Ask(this, $"「{d.Name}」の登録を取り消します。この端末からは記録を見られなくなります。よろしいですか?")) return;
        if (verb == "承認" && !Ui.Ask(this, $"「{d.Name}」を子機として承認します。\n見覚えのある端末ですか?")) return;
        await act(d); await RefreshDevices();
    }

    // ---------- 2. 職員 ----------
    private UIElement StaffTab()
    {
        Cols(_staff, ("お名前", 220, nameof(StaffRow.Name)), ("ログインID", 160, nameof(StaffRow.LoginId)), ("権限", 100, nameof(StaffRow.Role)), ("印刷・PDF", 110, nameof(StaffRow.Print)), ("状態", 100, nameof(StaffRow.Active)));
        return Tab(_staff,
            Ui.Btn("職員を追加", () => Safe(AddStaff), primary: true, minWidth: 130),
            Ui.Btn("パスワード再設定", () => Safe(ResetPw), minWidth: 170),
            Ui.Btn("印刷・PDF権限の切替", () => Safe(TogglePrint), minWidth: 200),
            Ui.Btn("無効にする / 有効に戻す", () => Safe(ToggleActive), minWidth: 210));
    }

    private async Task RefreshStaff()
    {
        _staffList = await _st.Api.Staff();
        _staff.ItemsSource = _staffList.Select(s => new StaffRow(s.Name, s.LoginId, s.IsAdmin ? "管理者" : "職員", s.CanPrint ? "許可" : "—", s.Active ? "使用中" : "無効")).ToList();
    }

    private static string NewPassword()
    {
        const string a = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var s = new StringBuilder();
        for (var i = 0; i < 12; i++) s.Append(a[RandomNumberGenerator.GetInt32(a.Length)]);
        return s.ToString();
    }

    private async Task AddStaff()
    {
        var v = Ui.Prompt(this, "職員を追加", "個人ごとのアカウントを作ります(共有アカウントは作らないでください)。初期パスワードは本人に直接伝え、最初のログインで必ず本人が変更します。",
            ("ログインID(半角英数字3文字以上)", false, ""), ("お名前", false, ""), ("初期パスワード(自動作成。変更も可)", false, NewPassword()));
        if (v == null) return;
        var print = Ui.Ask(this, "この職員に、日付別一覧の印刷・PDF出力を許可しますか?\n(個人情報が紙やファイルに出ます。必要な人だけに許可してください)", "印刷・PDF権限");
        await _st.Api.CreateStaff(new StaffCreateRequest(v[0], v[1], v[2], print));
        Ui.Info(this, $"追加しました。\n\nログインID: {v[0].Trim()}\n初期パスワード: {v[2]}\n\n本人に直接伝えてください。この画面を閉じると、パスワードは再表示できません。");
        await RefreshStaff();
    }

    private StaffInfo? SelStaff() { var i = _staff.SelectedIndex; if (i < 0) { Ui.Info(this, "一覧から職員を選んでください。"); return null; } return _staffList[i]; }

    private async Task ResetPw()
    {
        if (SelStaff() is not { } s) return;
        var v = Ui.Prompt(this, "パスワード再設定", $"{s.Name}さんの新しい初期パスワードを決めます。本人が次回ログイン時に変更します。", ("新しい初期パスワード", false, NewPassword()));
        if (v == null) return;
        await _st.Api.ResetPassword(s.Id, v[0]);
        Ui.Info(this, $"再設定しました。\n初期パスワード: {v[0]}\n本人に直接伝えてください。");
    }

    private async Task TogglePrint()
    {
        if (SelStaff() is not { } s) return;
        await _st.Api.SetCanPrint(s.Id, !s.CanPrint); await RefreshStaff();
    }

    private async Task ToggleActive()
    {
        if (SelStaff() is not { } s) return;
        var msg = s.Active ? $"{s.Name}さんのアカウントを無効にします(退職など)。すぐにログインできなくなります。よろしいですか?" : $"{s.Name}さんのアカウントを有効に戻します。よろしいですか?";
        if (!Ui.Ask(this, msg)) return;
        await _st.Api.SetActive(s.Id, !s.Active); await RefreshStaff();
    }

    // ---------- 3. バックアップ ----------
    private UIElement BackupTab()
    {
        Cols(_bkFiles, ("ファイル名", 280, nameof(FileRow.Name)), ("保存場所", 120, nameof(FileRow.Where)), ("サイズ", 100, nameof(FileRow.Size)), ("作成日時", 180, nameof(FileRow.At)));
        var top = new StackPanel();
        top.Children.Add(_bkStatus); top.Children.Add(_bkWarn);
        var btns = new WrapPanel
        {
            Children =
            {
                Ui.Btn("今すぐバックアップ", () => Safe(RunBackup), primary: true, minWidth: 190),
                Ui.Btn("別の媒体(USB等)を設定", () => Safe(SetExternal), minWidth: 210),
                Ui.Btn("バックアップを確認", () => Safe(VerifyBackups), minWidth: 170),
                Ui.Btn("選んだバックアップから復元", () => Safe(Restore), bg: Ui.Warn, minWidth: 230),
                Ui.Btn("復旧キーを再発行", () => Safe(Reissue), minWidth: 170),
            }
        };
        top.Children.Add(btns);
        top.Children.Add(Ui.Text("【定期的な復元試験】月1回、「バックアップを確認」を押して全件が「正常」になること、年1回は別のパソコンで復元できることを確認してください(手順書参照)。\n【別媒体】USBメモリ・外付けHDDを親機に挿し、「別の媒体を設定」で選ぶと、毎日自動でそこにも保存されます。", 13, false, Ui.Muted));
        var g = new Grid { Margin = new Thickness(10) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(top); Grid.SetRow(_bkFiles, 1); g.Children.Add(_bkFiles);
        return g;
    }

    private async Task RefreshBackup()
    {
        _bk = await _st.Api.BackupStatus();
        var b = _bk;
        _bkStatus.Text = $"親機内の最新: {b.LastLocalAt ?? "なし"}\n別媒体の最新: {b.LastExternalAt ?? "なし"}    別媒体の場所: {b.ExternalPath ?? "未設定"}{(b.ExternalPath != null && !b.ExternalReachable ? "  (接続できません)" : "")}\n親機の空き容量: {b.FreeBytes / (1 << 20):N0} MB    データの大きさ: {b.DbBytes / 1024:N0} KB";
        _bkWarn.Text = string.Join("\n", b.Warnings.Select(w => "⚠ " + w));
        _bkFiles.ItemsSource = b.Files.Select(f => new FileRow(f.Name, f.Location == "local" ? "親機内" : "別媒体", $"{f.Size / 1024:N0} KB", f.CreatedAt)).ToList();
    }

    private async Task RunBackup() { await _st.Api.RunBackup(); await RefreshBackup(); Ui.Info(this, "バックアップを作成し、検証しました。"); }

    private async Task SetExternal()
    {
        if (!_st.IsParent) { Ui.Info(this, "保存先の指定は、親機のパソコンで行ってください(USBメモリは親機に挿します)。"); return; }
        var d = new OpenFolderDialog { Title = "バックアップを保存する場所(USBメモリ・外付けHDDなど)を選んでください" };
        if (d.ShowDialog(this) != true) return;
        await _st.Api.SetBackupPath(d.FolderName); await RefreshBackup();
    }

    private async Task VerifyBackups()
    {
        var r = await _st.Api.VerifyBackups();
        Ui.Info(this, r.Count == 0 ? "バックアップがありません。" : $"{r.Count}件中 {r.Count(x => x.Ok)}件が正常です。\n" + (r.All(x => x.Ok) ? "すべて正常に開けました。" : "異常があるのは: " + string.Join("、", r.Where(x => !x.Ok).Select(x => x.Name)) + "\n新しいバックアップを作成してください。"));
    }

    private async Task Restore()
    {
        var i = _bkFiles.SelectedIndex;
        if (i < 0 || _bk == null) { Ui.Info(this, "復元するバックアップを一覧から選んでください。"); return; }
        var f = _bk.Files[i];
        if (!Ui.Ask(this, $"「{f.Name}」({f.CreatedAt})の状態に戻します。\n\n・それ以降に入力された記録は、この復元で見えなくなります(復元前の状態は自動で保存されます)\n・全員がいったんログアウトされます\n\n本当に復元しますか?", "復元の確認")) return;
        var v = Ui.Prompt(this, "復旧キーの入力", "控えている復旧キー(英数字40文字)を入力してください。", ("復旧キー", false, ""));
        if (v == null) return;
        await _st.Api.Restore(f.Location, f.Name, v[0]);
        Ui.Info(this, "復元しました。アプリをいったん閉じます。開き直して、あらためてログインしてください。");
        Application.Current.Shutdown();
    }

    private async Task Reissue()
    {
        if (!Ui.Ask(this, "復旧キーを新しく作り直します。古い復旧キーでは、これから作るバックアップを開けなくなります。\n(すでにあるバックアップは古いキーのままです)\n続けますか?")) return;
        var k = await _st.Api.ReissueRecoveryKey();
        new RecoveryKeyWindow(k.RecoveryKey) { Owner = this }.ShowDialog();
        await _st.Api.RunBackup(); await RefreshBackup();
    }

    // ---------- 4. 操作履歴 ----------
    private UIElement AuditTab()
    {
        Cols(_audit, ("日時", 170, nameof(AuditRow.At)), ("職員", 150, nameof(AuditRow.Staff)), ("操作", 220, nameof(AuditRow.Action)), ("内容", 420, nameof(AuditRow.Detail)));
        var g = Tab(_audit, Ui.Btn("更新", () => Safe(RefreshAudit), minWidth: 90), Ui.Btn("改ざんチェック", () => Safe(CheckChain), minWidth: 150), _auditState);
        return g;
    }

    private async Task RefreshAudit()
    {
        var a = await _st.Api.Audit(500);
        _audit.ItemsSource = a.Select(x => new AuditRow(x.At.Replace('T', ' ')[..16], x.StaffName, ActionNames.GetValueOrDefault(x.Action, x.Action), x.Detail)).ToList();
    }

    private async Task CheckChain()
    {
        var s = await _st.Api.SystemStatus();
        _auditState.Text = s.AuditChainOk ? "✓ 操作履歴に改ざんの形跡はありません" : "⚠ 操作履歴に不整合があります。バックアップの確認と、親機の管理者への連絡をしてください";
        _auditState.Foreground = s.AuditChainOk ? Ui.Good : Ui.Bad;
    }

    // ---------- 5. システム ----------
    private UIElement SystemTab()
    {
        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(_sys);
        sp.Children.Add(Ui.Btn("状態を更新", () => Safe(RefreshSystem), minWidth: 150));
        sp.Children.Add(Ui.Text("\n【アプリの更新】新しい版のインストーラーを、親機 → 子機の順に実行してください(上書きインストールで、記録データは消えません)。インターネットからの自動更新は行いません。\n【Windowsの更新】通常どおり更新してください。更新後に親機が再起動しても、共有サービスは自動で再開します。", 14, false, Ui.Muted));
        return sp;
    }

    private async Task RefreshSystem()
    {
        var s = await _st.Api.SystemStatus();
        _sys.Text = $"アプリのバージョン: {s.Version}\n親機の名前: {s.ServerName}\nデータの状態: {(s.DbOk ? "正常" : "異常あり(バックアップからの復元を検討)")}\n操作履歴の整合性: {(s.AuditChainOk ? "正常" : "不整合あり")}\n有効な職員: {s.StaffActive}人   使用中の端末: {s.DevicesApproved}台\n最新のバックアップ: {s.LastBackupAt ?? "なし"}";
    }
}
