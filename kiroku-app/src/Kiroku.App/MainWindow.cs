using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kiroku.Client;
using Kiroku.Core;

namespace Kiroku.App;

/// <summary>日々の記録(左: 本日の利用者一覧 / 右: 入力 / 下: 終礼メモ)。</summary>
internal sealed class MainWindow : AppWindow
{
    private readonly AppState _st;
    public bool LoggedOut { get; private set; }

    // 状態
    private DateTime _day = DateTime.Today;
    private List<RecordSummary> _list = [];
    private List<ClientInfo> _clients = [];
    private ClientInfo? _client;
    private string? _recordId;
    private int _version;
    private bool _dirty, _loading, _offline, _busy, _memoDirty;
    private int _memoVersion;
    private long _rev = -1;
    private RecordDetail? _saved;          // 画面に読み込んだ「保存済み」の記録(コピーはここから取る)
    private MemoDetail? _savedMemo;
    private readonly List<Action> _copyRefresh = [];
    private readonly TextBlock _toast = Ui.Text("", 15, true, Brushes.White);
    private readonly TextBlock _dateReadable = Ui.Text("", 15, true, Ui.Primary);
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    // 上部
    private readonly DatePicker _topDate = new() { FontSize = 18, MinHeight = 40, SelectedDateFormat = DatePickerFormat.Long };
    private readonly TextBlock _counts = Ui.Text("", 16, true, Brushes.White);
    private readonly TextBlock _state = Ui.Text("", 15, true, Brushes.White);
    private readonly Border _banner = new() { Background = Ui.Bad, Padding = new Thickness(12, 6, 12, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock _bannerText = Ui.Text("", 15, true, Brushes.White);

    // 左
    private readonly ListView _listView = new() { FontSize = 16, BorderBrush = Ui.Line };

    // 右
    private readonly TextBox _nameBox = Ui.Box();
    private readonly Popup _namePop = new() { StaysOpen = false, Placement = PlacementMode.Bottom, AllowsTransparency = false };
    private readonly ListBox _nameList = new() { FontSize = 17, MaxHeight = 260 };
    private readonly TextBlock _nameHint = Ui.Text("", 13, false, Ui.Warn);
    private readonly DatePicker _formDate = new() { FontSize = 17, MinHeight = 38, SelectedDateFormat = DatePickerFormat.Long };
    private readonly TextBox _in = Ui.Box(), _out = Ui.Box();
    private readonly TextBox _work = Ui.Box(true, 90), _notes = Ui.Box(true, 130);
    private readonly WrapPanel _chips = new();
    private readonly DispatcherTimer _suggestDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Button _saveBtn, _saveNextBtn;
    private readonly TextBlock _recInfo = Ui.Text("", 13, false, Ui.Muted);

    // 下(終礼メモ)
    private readonly TextBox _memo = Ui.Box(true, 90);
    private readonly TextBlock _memoInfo = Ui.Text("", 13, false, Ui.Muted);
    private readonly Button _memoSave;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow(AppState st)
    {
        _st = st;
        Ui.Style(this, "日々の記録", 1280, 860);
        MinWidth = 1000; MinHeight = 700; WindowState = WindowState.Maximized;

        _saveBtn = Ui.Btn("保存 (Ctrl+S)", () => Safe(() => SaveRecord(false)), primary: true, minWidth: 170);
        _saveNextBtn = Ui.Btn("保存して次の利用者へ", () => Safe(() => SaveRecord(true)), minWidth: 210);
        _memoSave = Ui.Btn("終礼メモを保存", () => Safe(SaveMemo), primary: true, minWidth: 170);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(BuildTop());
        Grid.SetRow(_banner, 1); _banner.Child = _bannerText; root.Children.Add(_banner);
        var mid = BuildMiddle(); Grid.SetRow(mid, 2); root.Children.Add(mid);
        var bottom = BuildBottom(); Grid.SetRow(bottom, 3); root.Children.Add(bottom);
        Content = root;

        // Ctrl+S
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Safe(SaveFocused)), Key.S, ModifierKeys.Control));

        _topDate.SelectedDate = _day; _formDate.SelectedDate = _day;
        _topDate.SelectedDateChanged += (_, _) => Safe(() => ChangeDay(_topDate.SelectedDate));
        _formDate.SelectedDateChanged += (_, _) => Safe(() => ChangeDay(_formDate.SelectedDate));

        _suggestDebounce.Tick += (_, _) => { _suggestDebounce.Stop(); Safe(RefreshSuggestions); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Text = ""; };
        _timer.Tick += (_, _) => Safe(Poll);
        Loaded += (_, _) => Safe(Start);
        Closing += OnClosing;
    }

    // ---------- 画面の組み立て ----------
    private UIElement BuildTop()
    {
        var bar = new Grid { Background = Ui.Header };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text("日々の記録", 24, true, Brushes.White); title.Margin = new Thickness(16, 8, 24, 8);
        bar.Children.Add(title);
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        dateRow.Children.Add(Ui.Btn("◀ 前日", () => Safe(() => ChangeDay(_day.AddDays(-1))), minWidth: 80));
        _topDate.Margin = new Thickness(4); _topDate.MinWidth = 220; dateRow.Children.Add(_topDate);
        dateRow.Children.Add(Ui.Btn("翌日 ▶", () => Safe(() => ChangeDay(_day.AddDays(1))), minWidth: 80));
        dateRow.Children.Add(Ui.Btn("今日", () => Safe(() => ChangeDay(DateTime.Today)), minWidth: 70));
        Grid.SetColumn(dateRow, 1); bar.Children.Add(dateRow);
        var mid = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _counts.Margin = new Thickness(0, 0, 24, 0); mid.Children.Add(_counts); mid.Children.Add(_state);
        _toast.Margin = new Thickness(24, 0, 0, 0); mid.Children.Add(_toast);
        Grid.SetColumn(mid, 2); bar.Children.Add(mid);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 8, 6) };
        right.Children.Add(Ui.Text(_st.Staff.Name + " さん", 15, false, Brushes.White));
        right.Children.Add(Ui.Btn("過去の記録", () => Safe(OpenHistory), minWidth: 110));
        if (_st.Staff.IsAdmin) right.Children.Add(Ui.Btn("管理", () => Safe(OpenAdmin), minWidth: 80));
        right.Children.Add(Ui.Btn("ログアウト", () => Safe(Logout), minWidth: 110));
        Grid.SetColumn(right, 3); bar.Children.Add(right);
        return bar;
    }

    private UIElement BuildMiddle()
    {
        var g = new Grid { Margin = new Thickness(10, 10, 10, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左: 本日の利用者一覧
        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        left.Children.Add(Dock(Ui.Text("本日の利用者", 18, true), Dock_.Top));
        var add = Ui.Btn("＋ 利用者を追加", () => Safe(AddClient), primary: true, minWidth: 380);
        left.Children.Add(Dock(add, Dock_.Bottom));
        var gv = new GridView();
        gv.Columns.Add(Col("氏名", 150, nameof(Row.Name)));
        gv.Columns.Add(Col("通所", 60, nameof(Row.In)));
        gv.Columns.Add(Col("退所", 60, nameof(Row.Out)));
        gv.Columns.Add(Col("状況", 90, nameof(Row.Status)));
        _listView.View = gv;
        _listView.SelectionChanged += (_, _) => { if (!_selecting && _listView.SelectedItem is Row r) Safe(() => SelectRow(r)); };
        left.Children.Add(_listView);
        g.Children.Add(left);

        // 右: 入力
        var form = new StackPanel();
        var nameGrid = new Grid();
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameGrid.Children.Add(_nameBox);
        var drop = Ui.Btn("▼ 一覧から選ぶ", () => ShowNameList(all: true), minWidth: 130); drop.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(drop, 1); nameGrid.Children.Add(drop);
        _namePop.Child = new Border { Child = _nameList, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Background = Brushes.White };
        _namePop.PlacementTarget = _nameBox; _nameList.DisplayMemberPath = nameof(ClientInfo.Name);
        _nameList.MouseUp += (_, _) => PickFromList();
        _nameList.KeyDown += (_, e) => { if (e.Key == Key.Enter) PickFromList(); };
        _nameBox.TextChanged += (_, _) => OnNameTyped();
        _nameBox.LostKeyboardFocus += (_, _) => Safe(OnNameCommitted);
        _nameBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Down && _namePop.IsOpen) { _nameList.Focus(); if (_nameList.Items.Count > 0) _nameList.SelectedIndex = 0; e.Handled = true; } };
        var nameField = Ui.Field("氏名", nameGrid, null, CopyBtn(CopyValues.Name));
        nameField.Children.Add(_nameHint);
        var dateField = Ui.Field("日付", _formDate, null, CopyBtn(CopyValues.Date));
        dateField.Children.Add(_dateReadable);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(nameField); dateField.Margin = new Thickness(12, 0, 0, 8); Grid.SetColumn(dateField, 1); top.Children.Add(dateField);
        form.Children.Add(top);

        var times = new Grid();
        for (var i = 0; i < 2; i++) times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        times.Children.Add(TimeField("通所時間", _in, 0, CopyValues.CheckIn));
        times.Children.Add(TimeField("退所時間", _out, 1, CopyValues.CheckOut));
        form.Children.Add(times);

        form.Children.Add(Ui.Field("作業内容", _work, null, CopyBtn(CopyValues.Work)));
        _chips.Margin = new Thickness(0, -4, 0, 8); form.Children.Add(_chips);
        form.Children.Add(Ui.Field("利用者の様子(支援状況・体調・作業への取り組み・職員の対応など)", _notes, null, CopyBtn(CopyValues.Notes)));

        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(_saveBtn); btns.Children.Add(_saveNextBtn); btns.Children.Add(_recInfo);
        _recInfo.Margin = new Thickness(12, 0, 0, 0);
        form.Children.Add(btns);

        foreach (var tb in new[] { _in, _out, _work, _notes, _nameBox }) tb.TextChanged += (_, _) => { if (!_loading) MarkDirty(); };
        _work.TextChanged += (_, _) => { if (!_loading) { _suggestDebounce.Stop(); _suggestDebounce.Start(); } };

        // タブ順: 氏名 → 日付 → 通所 → 退所 → 作業 → 様子 → 保存
        _nameBox.TabIndex = 0; _formDate.TabIndex = 1; _in.TabIndex = 2; _out.TabIndex = 3; _work.TabIndex = 4; _notes.TabIndex = 5; _saveBtn.TabIndex = 6;
        _work.AcceptsTab = false; _notes.AcceptsTab = false;

        var scroll = new ScrollViewer { Content = Ui.Card(form), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1); g.Children.Add(scroll);
        return g;
    }

    private UIElement BuildBottom()
    {
        var sp = new Grid { Margin = new Thickness(10, 0, 10, 10) };
        sp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Text("終礼メモ(この日の共通メモ)", 18, true)); head.Children.Add(MemoCopyBtn()); _memoInfo.Margin = new Thickness(14, 0, 0, 0); head.Children.Add(_memoInfo);
        left.Children.Add(head); left.Children.Add(_memo);
        _memo.TextChanged += (_, _) => { if (!_loading) { _memoDirty = true; UpdateState(); } };
        sp.Children.Add(left);
        var b = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom }; b.Children.Add(_memoSave);
        Grid.SetColumn(b, 1); sp.Children.Add(b);
        return Ui.Card(sp, new Thickness(10, 0, 10, 10));
    }

    private UIElement TimeField(string label, TextBox box, int col, Func<RecordDetail?, string?> pick)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(box);
        var now = Ui.Btn("いま", () => { box.Text = DateTime.Now.ToString("HH:mm"); box.Focus(); }, minWidth: 70); now.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(now, 1); row.Children.Add(now);
        var f = Ui.Field(label, row, "例: 9:30 や 0930(空のままでも保存できます)", CopyBtn(pick));
        f.Margin = new Thickness(col == 0 ? 0 : 12, 0, 0, 8);
        Grid.SetColumn(f, col);
        return f;
    }

    private Button CopyBtn(Func<RecordDetail?, string?> pick) => MakeCopy(() => pick(_saved), () => _dirty || _saved == null);
    private Button MemoCopyBtn() => MakeCopy(() => CopyValues.Memo(_savedMemo), () => _memoDirty);

    private Button MakeCopy(Func<string?> value, Func<bool> unsaved)
    {
        var b = CopyService.Button(value, unsaved, ReportCopy, out var refresh);
        _copyRefresh.Add(refresh); refresh();
        return b;
    }

    private void ReportCopy(string? error)
    {
        if (error != null) { Ui.Error(this, error, "コピーできませんでした"); return; }
        _toast.Text = "✓ コピーしました"; _toastTimer.Stop(); _toastTimer.Start();
    }

    private sealed record Row(string Id, string ClientId, string Name, string In, string Out, string Status, int Version);
    private static GridViewColumn Col(string h, double w, string path) => new() { Header = h, Width = w, DisplayMemberBinding = new System.Windows.Data.Binding(path) };
    private enum Dock_ { Top, Bottom }
    private static UIElement Dock(UIElement e, Dock_ d) { DockPanel.SetDock(e, d == Dock_.Top ? System.Windows.Controls.Dock.Top : System.Windows.Controls.Dock.Bottom); return e; }

    private sealed class RelayCommand(Action a) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => a();
    }

    // ---------- 起動・更新 ----------
    private bool _selecting;

    private async Task Start()
    {
        await RefreshClients();
        await ChangeDay(_day, force: true);
        var n = _st.Drafts.All().Count;
        if (n > 0) Ui.Info(this, $"保存できていなかった入力が{n}件、このパソコンに一時保存されています。\n該当の利用者と日付を選ぶと、復元するか確認します。");
        _timer.Start();
    }

    private async Task RefreshClients() => _clients = await _st.Api.Clients();

    private async Task RefreshList()
    {
        _list = await _st.Api.Records(AppState.DayText(_day));
        var rows = _list.Select(r => new Row(r.RecordId, r.ClientId, r.ClientName, r.CheckIn ?? "", r.CheckOut ?? "", StatusOf(r), r.Version)).ToList();
        _selecting = true;
        _listView.ItemsSource = rows;
        var cur = rows.FirstOrDefault(r => r.Id == _recordId);
        if (cur != null) _listView.SelectedItem = cur;
        _selecting = false;
        var done = _list.Count(r => StatusOf(r) == "入力済");
        _counts.Text = $"本日の利用人数: {_list.Count}人    入力済み: {done}件";
    }

    private static string StatusOf(RecordSummary r) =>
        r.CheckIn != null && r.CheckOut != null && (r.HasWork || r.HasNotes) ? "入力済" : r.CheckIn != null && r.CheckOut == null ? "通所のみ" : "一部入力";

    private async Task LoadMemo()
    {
        var m = await _st.Api.Memo(AppState.DayText(_day));
        _loading = true; _memo.Text = m.Text; _loading = false;
        _memoVersion = m.Version; _memoDirty = false; _savedMemo = m;
        _memoInfo.Text = m.UpdatedAt == null ? "まだ入力されていません" : $"最終更新: {m.UpdatedByName}  {FormatTime(m.UpdatedAt)}";
        UpdateState();
    }

    private static string FormatTime(string iso) => DateTimeOffset.TryParse(iso, out var d) ? d.ToLocalTime().ToString("M/d HH:mm") : iso;

    private async Task ChangeDay(DateTime? target, bool force = false)
    {
        if (target == null) return;
        if (!force && target.Value.Date == _day.Date) return;
        if (!force && !await ConfirmLeave()) { _topDate.SelectedDate = _day; _formDate.SelectedDate = _day; return; }
        _day = target.Value.Date;
        _topDate.SelectedDate = _day; _formDate.SelectedDate = _day;
        ClearForm();
        await RefreshList();
        await LoadMemo();
        Title = "日々の記録  " + AppState.DayLabel(_day);
    }

    // ---------- 変更検知・オンライン状態 ----------
    private async Task Poll()
    {
        if (_busy) return;
        try
        {
            var p = await _st.Api.Pulse();
            if (_offline) { _offline = false; _banner.Visibility = Visibility.Collapsed; await RefreshClients(); _rev = -1; }
            if (p.Revision != _rev)
            {
                _rev = p.Revision;
                await RefreshClients();
                await RefreshList();
                if (!_memoDirty) await LoadMemo();
                if (!_dirty && _recordId != null && _client != null) await ReloadCurrent();
            }
        }
        catch (KirokuException e) when (e.Code == "unreachable")
        {
            _offline = true; _banner.Visibility = Visibility.Visible;
            _bannerText.Text = "親機に接続できません。いまの入力はまだ保存されていません。つながるまで自動で再接続を試みます(画面の入力は消えません)。";
            UpdateState();
        }
        catch (KirokuException e) when (e.Code is "not_logged_in" or "device_unregistered")
        {
            _timer.Stop();
            Ui.Error(this, e.Message);
            if (!_dirty && !_memoDirty) { LoggedOut = true; Close(); }
        }
    }

    private async Task ReloadCurrent()
    {
        var d = await _st.Api.Record(_client!.Id, AppState.DayText(_day));
        if (d != null && d.Version != _version) FillForm(d);
    }

    private void UpdateState()
    {
        foreach (var r in _copyRefresh) r();
        _dateReadable.Text = AppState.DayLabel(_day);
        UpdateStateText();
    }

    private void UpdateStateText()
    {
        if (_offline) { _state.Text = "● 親機に接続できません(未保存)"; _state.Foreground = Brushes.Yellow; return; }
        if (_dirty || _memoDirty) { _state.Text = "● 未保存の変更があります"; _state.Foreground = Brushes.Yellow; return; }
        _state.Text = _lastSaved == null ? "" : "✓ 保存済み " + _lastSaved; _state.Foreground = Brushes.White;
    }
    private string? _lastSaved;
    private void MarkDirty() { _dirty = true; UpdateState(); }

    // ---------- 氏名入力 ----------
    private void OnNameTyped()
    {
        if (_loading) return;
        if (_client != null && _nameBox.Text != _client.Name) { _client = null; _recordId = null; _version = 0; _saved = null; _recInfo.Text = ""; }
        UpdateNameHint();
        if (_nameBox.IsKeyboardFocusWithin) ShowNameList(all: false);
    }

    private void UpdateNameHint()
    {
        var t = Names.Clean(_nameBox.Text);
        _nameHint.Text = _client != null || t == null ? "" :
            _clients.Any(c => Names.Key(c.Name) == Names.Key(t)) ? "同じ氏名の方が一覧にいます。同じ方なら一覧から選んでください。" :
            "初めての氏名です。保存すると利用者一覧に自動で登録されます。";
    }

    private void ShowNameList(bool all)
    {
        var q = all ? "" : Names.Key(_nameBox.Text ?? "");
        var items = _clients.Where(c => q == "" || Names.Key(c.Name).Contains(q)).Take(50).ToList();
        _nameList.ItemsSource = items;
        if (items.Count == 0) { _namePop.IsOpen = false; return; }
        _namePop.Width = Math.Max(_nameBox.ActualWidth, 300);
        _namePop.IsOpen = true;
    }

    private void PickFromList()
    {
        if (_nameList.SelectedItem is not ClientInfo c) return;
        _namePop.IsOpen = false;
        Safe(() => SelectClient(c));
    }

    /// <summary>入力欄から離れたとき、完全一致する人が1人なら自動でその人を選ぶ(重複登録の予防)。</summary>
    private async Task OnNameCommitted()
    {
        if (_client != null || _namePop.IsMouseOver || _nameList.IsKeyboardFocusWithin) return;
        var t = Names.Clean(_nameBox.Text);
        if (t == null) return;
        var m = _clients.Where(c => Names.Key(c.Name) == Names.Key(t)).ToList();
        if (m.Count == 1) await SelectClient(m[0]);
    }

    private async Task SelectClient(ClientInfo c)
    {
        // 入力途中の内容(氏名以外)があるときは、無言で消さない
        if (_dirty && _client?.Id != c.Id && (_in.Text != "" || _out.Text != "" || _work.Text != "" || _notes.Text != "")
            && !Ui.Ask(this, "入力途中の内容があります。別の利用者を選ぶと、入力した内容は消えます。よろしいですか?\n(保存する場合は「いいえ」を押して、先に保存してください)", "入力途中です"))
        { _loading = true; _nameBox.Text = _client?.Name ?? _nameBox.Text; _loading = false; return; }
        _client = c;
        _loading = true; _nameBox.Text = c.Name; _loading = false;
        _nameHint.Text = "";
        var d = await _st.Api.Record(c.Id, AppState.DayText(_day));
        if (d != null) FillForm(d); else { _recordId = null; _version = 0; _saved = null; ClearFields(keepName: true); _recInfo.Text = "この日の記録はまだありません(新規)"; }
        await OfferDraft();
        if (d == null) _in.Focus(); else _out.Focus();
    }

    // ---------- 一覧・フォーム ----------
    private async Task SelectRow(Row r)
    {
        if (r.Id == _recordId) return;
        if (!await ConfirmLeave()) { _selecting = true; _listView.SelectedItem = (_listView.ItemsSource as List<Row>)?.FirstOrDefault(x => x.Id == _recordId); _selecting = false; return; }
        var d = await _st.Api.GetRecord(r.Id);
        _client = new ClientInfo(d.ClientId, d.ClientName);
        FillForm(d);
        await OfferDraft();
    }

    private async Task AddClient()
    {
        if (!await ConfirmLeave()) return;
        ClearForm();
        _selecting = true; _listView.SelectedItem = null; _selecting = false;
        _nameBox.Focus();
    }

    private void FillForm(RecordDetail d)
    {
        _loading = true;
        _client = new ClientInfo(d.ClientId, d.ClientName);
        _nameBox.Text = d.ClientName; _in.Text = d.CheckIn ?? ""; _out.Text = d.CheckOut ?? "";
        _work.Text = d.Work; _notes.Text = d.Notes;
        _recordId = d.Id; _version = d.Version; _dirty = false; _saved = d;
        _recInfo.Text = $"最終更新: {d.UpdatedByName}  {FormatTime(d.UpdatedAt)}";
        _nameHint.Text = "";
        _loading = false;
        UpdateState();
        _selecting = true;
        _listView.SelectedItem = (_listView.ItemsSource as List<Row>)?.FirstOrDefault(x => x.Id == d.Id);
        _selecting = false;
    }

    private void ClearFields(bool keepName)
    {
        _loading = true;
        if (!keepName) _nameBox.Text = "";
        _in.Text = ""; _out.Text = ""; _work.Text = ""; _notes.Text = ""; _chips.Children.Clear();
        _dirty = false; _loading = false; UpdateState();
    }

    private void ClearForm()
    {
        _client = null; _recordId = null; _version = 0; _saved = null; _recInfo.Text = ""; _nameHint.Text = "";
        ClearFields(keepName: false);
    }

    // ---------- 作業内容の候補 ----------
    private async Task RefreshSuggestions()
    {
        if (_offline) return;
        var q = _work.Text.Split('\n').LastOrDefault()?.Trim() ?? "";
        var s = await _st.Api.WorkSuggestions(q);
        _chips.Children.Clear();
        foreach (var t in s.Where(x => x != _work.Text.Trim()).Take(8))
        {
            var text = t;
            var b = Ui.Btn(t.Length > 20 ? t[..20] + "…" : t, () => { _work.Text = text; _work.CaretIndex = text.Length; _work.Focus(); }, minWidth: 40);
            b.MinHeight = 34; b.FontSize = 14; b.ToolTip = "クリックで入力(あとから自由に直せます)";
            _chips.Children.Add(b);
        }
    }

    // ---------- 保存 ----------
    private async Task SaveFocused()
    {
        if (_memo.IsKeyboardFocusWithin) await SaveMemo(); else await SaveRecord(false);
    }

    private RecordSaveRequest BuildRequest(bool confirmDup = false, bool confirmTime = false, int? version = null, string? clientId = null) => new(
        clientId ?? _client?.Id, _client == null && clientId == null ? Names.Clean(_nameBox.Text) : null, AppState.DayText(_day),
        _in.Text, _out.Text, _work.Text, _notes.Text, version ?? _version, confirmDup, confirmTime);

    private async Task<bool> SaveRecord(bool next)
    {
        if (_busy) return false;
        if (Names.Clean(_nameBox.Text) == null && _client == null) { Ui.Error(this, "氏名を入力してください。"); _nameBox.Focus(); return false; }
        _busy = true; _saveBtn.IsEnabled = false; _saveNextBtn.IsEnabled = false; _state.Text = "保存しています…";
        try
        {
            var req = BuildRequest();
            while (true)
            {
                try
                {
                    var saved = await _st.Api.SaveRecord(req);
                    _st.Drafts.Delete("record", DraftKey());
                    FillForm(saved);
                    _lastSaved = DateTime.Now.ToString("HH:mm"); _dirty = false; UpdateState();
                    await RefreshClients(); await RefreshList();
                    if (next) { ClearForm(); _nameBox.Focus(); }
                    return true;
                }
                catch (KirokuException e) when (e.Code == "time_warning")
                {
                    if (!Ui.Ask(this, e.Message, "時間の確認")) return false;
                    req = req with { ConfirmTimeWarning = true };
                }
                catch (KirokuException e) when (e.Code == "duplicate_name")
                {
                    var same = Detail<List<ClientInfo>>(e);
                    var pick = Ui.Choose(this, "同じ氏名の方がいます", e.Message, same is { Count: 1 } ? "一覧にいる方として保存する" : "キャンセル", "別人(同姓同名)として新しく登録する", "キャンセル");
                    if (pick == 0 && same is { Count: 1 }) { _client = same[0]; req = BuildRequest(clientId: same[0].Id); var ex = await _st.Api.Record(same[0].Id, req.Date); req = req with { ExpectedVersion = ex?.Version ?? 0 }; }
                    else if (pick == 1) req = req with { ConfirmDuplicateName = true };
                    else return false;
                }
                catch (KirokuException e) when (e.Code == "conflict")
                {
                    var cur = Detail<RecordDetail>(e);
                    if (cur == null) { Ui.Error(this, e.Message); return false; }
                    var pick = Ui.Choose(this, "他の職員が先に保存しています",
                        $"{cur.UpdatedByName}さんが {FormatTime(cur.UpdatedAt)} にこの記録を保存しました。\n\n【相手の内容】\n通所 {cur.CheckIn} / 退所 {cur.CheckOut}\n作業: {Short(cur.Work)}\n様子: {Short(cur.Notes)}\n\nどうしますか?",
                        "相手の内容を表示する(自分の入力は破棄)", "自分の入力で上書きする(相手の内容は履歴に残ります)", "戻って確認する");
                    if (pick == 0) { FillForm(cur); return false; }
                    if (pick == 1) req = req with { ExpectedVersion = cur.Version };
                    else return false;
                }
            }
        }
        catch (KirokuException e) when (e.Code == "unreachable")
        {
            _offline = true; _banner.Visibility = Visibility.Visible;
            _bannerText.Text = "親機に接続できないため保存できていません。入力内容はこのパソコンに一時退避しました。つながったら、もう一度「保存」を押してください。";
            try { _st.Drafts.Save("record", DraftKey(), JsonSerializer.Serialize(new FormDraft(_in.Text, _out.Text, _work.Text, _notes.Text))); } catch { }
            UpdateState();
            Ui.Error(this, "保存できていません。\n\n" + e.Message);
            return false;
        }
        finally { _busy = false; _saveBtn.IsEnabled = true; _saveNextBtn.IsEnabled = true; }
    }

    private static string Short(string s) => string.IsNullOrWhiteSpace(s) ? "(なし)" : (s.Length > 60 ? s[..60] + "…" : s).Replace("\n", " ");

    private static T? Detail<T>(KirokuException e)
    {
        try
        {
            return e.Detail is JsonElement el ? el.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) : default;
        }
        catch (JsonException) { return default; }
    }

    private sealed record FormDraft(string In, string Out, string Work, string Notes);
    private string DraftKey() => (_client?.Id ?? "new:" + Names.Key(_nameBox.Text)) + "|" + AppState.DayText(_day);

    private async Task OfferDraft()
    {
        var d = _st.Drafts.Load("record", DraftKey());
        if (d == null) return;
        if (Ui.Ask(this, $"保存できていなかった入力(退避日時 {d.SavedAt.Replace('T', ' ')})があります。\n復元しますか?\n(「いいえ」を選ぶと退避した入力は破棄されます)"))
        {
            var f = JsonSerializer.Deserialize<FormDraft>(d.Json);
            if (f != null) { _loading = true; _in.Text = f.In; _out.Text = f.Out; _work.Text = f.Work; _notes.Text = f.Notes; _loading = false; MarkDirty(); }
        }
        else _st.Drafts.Delete("record", DraftKey());
        await Task.CompletedTask;
    }

    private async Task SaveMemo()
    {
        if (_busy) return;
        _busy = true; _memoSave.IsEnabled = false;
        try
        {
            var text = _memo.Text; var ver = _memoVersion;
            while (true)
            {
                try
                {
                    var m = await _st.Api.SaveMemo(new MemoSaveRequest(AppState.DayText(_day), text, ver));
                    _memoVersion = m.Version; _memoDirty = false; _savedMemo = m;
                    _memoInfo.Text = m.UpdatedAt == null ? "" : $"最終更新: {m.UpdatedByName}  {FormatTime(m.UpdatedAt)}";
                    _lastSaved = DateTime.Now.ToString("HH:mm"); UpdateState();
                    return;
                }
                catch (KirokuException e) when (e.Code == "conflict")
                {
                    var cur = Detail<MemoDetail>(e);
                    if (cur == null) { Ui.Error(this, e.Message); return; }
                    var pick = Ui.Choose(this, "他の職員が先に終礼メモを保存しています",
                        $"{cur.UpdatedByName}さんが {FormatTime(cur.UpdatedAt ?? "")} に保存しました。相手の内容を消さないよう、保存を止めています。\n\n【相手の内容】\n{Short(cur.Text)}",
                        "相手の内容のあとに、自分の入力を付け足して保存する(両方残る)", "相手の内容を表示する(自分の入力は破棄)", "自分の入力で上書きする(相手の内容は履歴に残ります)", "戻って確認する");
                    if (pick == 0) { text = cur.Text.TrimEnd() + "\n" + text; ver = cur.Version; }
                    else if (pick == 1) { _loading = true; _memo.Text = cur.Text; _loading = false; _memoVersion = cur.Version; _memoDirty = false; _savedMemo = cur; UpdateState(); return; }
                    else if (pick == 2) ver = cur.Version;
                    else return;
                }
            }
        }
        catch (KirokuException e) when (e.Code == "unreachable")
        {
            _offline = true; _banner.Visibility = Visibility.Visible; _bannerText.Text = "親機に接続できないため終礼メモは保存できていません。入力は画面に残っています。";
            Ui.Error(this, "保存できていません。\n\n" + e.Message);
        }
        finally { _busy = false; _memoSave.IsEnabled = true; }
    }

    // ---------- 画面移動の確認 ----------
    private async Task<bool> ConfirmLeave()
    {
        if (_dirty)
        {
            var pick = Ui.Choose(this, "保存されていません", "入力した内容がまだ保存されていません。", "保存してから移動する", "保存せずに移動する(入力は消えます)", "戻る");
            if (pick == 0) { if (!await SaveRecord(false)) return false; }
            else if (pick != 1) return false;
            _dirty = false;
        }
        if (_memoDirty)
        {
            var pick = Ui.Choose(this, "終礼メモが保存されていません", "終礼メモの変更がまだ保存されていません。", "保存してから移動する", "保存せずに移動する(入力は消えます)", "戻る");
            if (pick == 0) { await SaveMemo(); if (_memoDirty) return false; }
            else if (pick != 1) return false;
            _memoDirty = false;
        }
        return true;
    }

    private bool _closing;
    private void OnClosing(object? s, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing || (!_dirty && !_memoDirty)) { _timer.Stop(); return; }
        e.Cancel = true;
        Safe(async () => { if (await ConfirmLeave()) { _closing = true; Close(); } });
    }

    // ---------- 他画面 ----------
    private async Task Logout()
    {
        if (!await ConfirmLeave()) return;
        LoggedOut = true; _closing = true; Close();
    }

    private async Task OpenHistory()
    {
        if (!await ConfirmLeave()) return;
        var w = new HistoryWindow(_st, _day) { Owner = this };
        w.ShowDialog();
        if (w.OpenTarget is { } t)
        {
            await ChangeDay(t.Date, force: true);
            var c = _clients.FirstOrDefault(x => x.Id == t.ClientId);
            if (c != null) await SelectClient(c);
        }
        else await RefreshList();
    }

    private Task OpenAdmin()
    {
        new AdminWindow(_st) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }
}
