using System.Text;
using System.Windows;
using System.Windows.Controls;
using Kiroku.Core;

namespace Kiroku.App;

/// <summary>過去の記録: 日付別一覧・氏名検索・閲覧・修正(日々の記録画面で)・修正履歴・印刷/PDF。</summary>
internal sealed class HistoryWindow : AppWindow
{
    public (DateTime Date, string ClientId)? OpenTarget { get; private set; }
    private readonly AppState _st;
    private readonly TextBox _name = Ui.Box();
    private readonly DatePicker _from = new() { FontSize = 16, MinHeight = 38 }, _to = new() { FontSize = 16, MinHeight = 38 };
    private readonly ListView _list = new() { FontSize = 15 };
    private readonly TextBlock _detail = Ui.Text("左の一覧から記録を選んでください。", 16);
    private readonly TextBlock _count = Ui.Text("", 14, false, Ui.Muted);
    private readonly Button _openBtn, _histBtn, _printBtn, _pdfBtn;
    private List<RecordSummary> _results = [];
    private RecordDetail? _cur;

    private sealed record Row(string Date, string Name, string In, string Out, string Status);

    public HistoryWindow(AppState st, DateTime day)
    {
        _st = st;
        Ui.Style(this, "過去の記録", 1180, 760);
        _from.SelectedDate = day.AddDays(-30); _to.SelectedDate = day;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var f = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        f.Children.Add(Ui.Field("氏名(一部でも可)", Sized(_name, 220)));
        f.Children.Add(Ui.Field("開始日", Sized(_from, 200)));
        f.Children.Add(Ui.Field("終了日", Sized(_to, 200)));
        var bs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
        bs.Children.Add(Ui.Btn("検索", () => Safe(Search), primary: true, minWidth: 90));
        bs.Children.Add(Ui.Btn("この日(終了日)だけ表示", () => { _from.SelectedDate = _to.SelectedDate; Safe(Search); }, minWidth: 190));
        f.Children.Add(bs);
        root.Children.Add(f);

        var body = new Grid(); Grid.SetRow(body, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(520) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        _count.Margin = new Thickness(0, 4, 0, 0); DockPanel.SetDock(_count, Dock.Bottom); left.Children.Add(_count);
        var gv = new GridView();
        foreach (var (h, w, p) in new[] { ("日付", 100, nameof(Row.Date)), ("氏名", 150, nameof(Row.Name)), ("通所", 60, nameof(Row.In)), ("退所", 60, nameof(Row.Out)), ("状況", 80, nameof(Row.Status)) })
            gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new System.Windows.Data.Binding(p) });
        _list.View = gv;
        _list.SelectionChanged += (_, _) => Safe(ShowSelected);
        left.Children.Add(_list);
        body.Children.Add(left);

        var right = new DockPanel();
        var btns = new WrapPanel(); DockPanel.SetDock(btns, Dock.Bottom);
        _openBtn = Ui.Btn("この記録を修正する(日々の記録で開く)", OpenForEdit, primary: true, minWidth: 330);
        _histBtn = Ui.Btn("修正履歴を見る", () => Safe(ShowHistory), minWidth: 150);
        _printBtn = Ui.Btn("この日の一覧を印刷", () => Safe(() => Print(false)), minWidth: 180);
        _pdfBtn = Ui.Btn("この日の一覧をPDF保存", () => Safe(() => Print(true)), minWidth: 190);
        btns.Children.Add(_openBtn); btns.Children.Add(_histBtn); btns.Children.Add(_printBtn); btns.Children.Add(_pdfBtn);
        if (!st.Staff.CanPrint)
        {
            foreach (var b in new[] { _printBtn, _pdfBtn }) { b.IsEnabled = false; b.ToolTip = "印刷・PDF出力の権限がありません。管理者に依頼してください。"; }
        }
        right.Children.Add(btns);
        right.Children.Add(new ScrollViewer { Content = Ui.Card(_detail), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(right, 1); body.Children.Add(right);
        root.Children.Add(body);
        Content = root;
        _openBtn.IsEnabled = _histBtn.IsEnabled = false;
        Loaded += (_, _) => Safe(Search);
    }

    private static FrameworkElement Sized(FrameworkElement e, double w) { e.Width = w; return e; }

    private async Task Search()
    {
        _results = await _st.Api.Search(_name.Text.Trim(), Day(_from), Day(_to));
        _list.ItemsSource = _results.Select(r => new Row(r.Date, r.ClientName, r.CheckIn ?? "", r.CheckOut ?? "",
            r.CheckIn != null && r.CheckOut != null && (r.HasWork || r.HasNotes) ? "入力済" : r.CheckIn != null && r.CheckOut == null ? "通所のみ" : "一部入力")).ToList();
        _count.Text = _results.Count >= 500 ? "500件を超えています。期間や氏名を絞ってください。" : $"{_results.Count}件";
        _cur = null; _detail.Text = "左の一覧から記録を選んでください。"; _openBtn.IsEnabled = _histBtn.IsEnabled = false;
    }

    private static string? Day(DatePicker p) => p.SelectedDate?.ToString("yyyy-MM-dd");

    private async Task ShowSelected()
    {
        var i = _list.SelectedIndex;
        if (i < 0 || i >= _results.Count) return;
        _cur = await _st.Api.GetRecord(_results[i].RecordId);
        var d = _cur;
        _detail.Text = $"{d.ClientName} さん   {d.Date}\n\n通所: {d.CheckIn ?? "(未入力)"}    退所: {d.CheckOut ?? "(未入力)"}\n\n【作業内容】\n{(d.Work == "" ? "(なし)" : d.Work)}\n\n【利用者の様子】\n{(d.Notes == "" ? "(なし)" : d.Notes)}\n\n最終更新: {d.UpdatedByName}   {d.UpdatedAt.Replace('T', ' ')}   (第{d.Version}版)";
        _openBtn.IsEnabled = _histBtn.IsEnabled = true;
    }

    private void OpenForEdit()
    {
        if (_cur == null) return;
        OpenTarget = (DateTime.ParseExact(_cur.Date, "yyyy-MM-dd", null), _cur.ClientId);
        Close();
    }

    private async Task ShowHistory()
    {
        if (_cur == null) return;
        var h = await _st.Api.RecordHistory(_cur.Id);
        var sb = new StringBuilder();
        sb.AppendLine($"現在: 第{_cur.Version}版  {_cur.UpdatedByName}  {_cur.UpdatedAt.Replace('T', ' ')}");
        sb.AppendLine("(以下は、修正される前の内容です)\n");
        if (h.Count == 0) sb.AppendLine("修正はありません。");
        foreach (var x in h)
            sb.AppendLine($"■ 第{x.Version}版 → {x.ReplacedByName}さんが {x.ReplacedAt.Replace('T', ' ')} に修正\n  通所 {x.CheckIn ?? "(未)"} / 退所 {x.CheckOut ?? "(未)"}\n  作業: {x.Work}\n  様子: {x.Notes}\n");
        var w = new Window { Owner = this };
        Ui.Style(w, "修正履歴", 700, 560); w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.Content = new TextBox { Text = sb.ToString(), IsReadOnly = true, FontSize = 15, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10) };
        w.ShowDialog();
    }

    private async Task Print(bool pdf)
    {
        var day = _cur != null ? DateTime.ParseExact(_cur.Date, "yyyy-MM-dd", null) : (_to.SelectedDate ?? DateTime.Today);
        await PrintService.PrintDay(this, _st, day, pdf);
    }
}
