using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Kiroku.Core;

namespace Kiroku.App;

/// <summary>日付別一覧の印刷・PDF出力。権限は親機が判定し、出力の事実は操作履歴に残る。PDFはWindows標準の「Microsoft Print to PDF」を使う。</summary>
internal static class PrintService
{
    public static async Task PrintDay(Window owner, AppState st, DateTime day, bool pdf)
    {
        await st.Api.AuthorizePrint("daily", AppState.DayText(day)); // 権限がなければここで日本語の拒否メッセージ
        var list = await st.Api.Records(AppState.DayText(day));
        if (list.Count == 0) { Ui.Info(owner, "この日の記録はありません。"); return; }
        var details = new List<RecordDetail>();
        foreach (var r in list) details.Add(await st.Api.GetRecord(r.RecordId));
        var memo = await st.Api.Memo(AppState.DayText(day));

        var pd = new PrintDialog();
        if (pdf)
        {
            try
            {
                using var server = new LocalPrintServer();
                pd.PrintQueue = server.GetPrintQueue("Microsoft Print to PDF");
            }
            catch (Exception)
            {
                Ui.Error(owner, "この端末に「Microsoft Print to PDF」が見つかりません。\n「印刷」ボタンからPDF対応のプリンターを選んでください。");
                return;
            }
        }
        else if (pd.ShowDialog() != true) return;

        var doc = Build(day, details, memo, st.Staff.Name, pd.PrintableAreaWidth, pd.PrintableAreaHeight);
        pd.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, "日々の記録 " + AppState.DayText(day));
    }

    private static FlowDocument Build(DateTime day, List<RecordDetail> items, MemoDetail memo, string staff, double w, double h)
    {
        var doc = new FlowDocument
        {
            FontFamily = Ui.Font, FontSize = 11, PagePadding = new Thickness(36), PageWidth = w > 0 ? w : 793, PageHeight = h > 0 ? h : 1122, ColumnWidth = 10000,
        };
        doc.Blocks.Add(new Paragraph(new Run("日々の記録  " + AppState.DayLabel(day))) { FontSize = 20, FontWeight = FontWeights.Bold });
        doc.Blocks.Add(new Paragraph(new Run($"利用人数 {items.Count}人")) { FontSize = 12, Foreground = Brushes.DimGray });

        var t = new Table { CellSpacing = 0 };
        foreach (var width in new[] { 90.0, 90, 160, 280 }) t.Columns.Add(new TableColumn { Width = new GridLength(width) });
        var g = new TableRowGroup(); t.RowGroups.Add(g);
        g.Rows.Add(Row(true, "氏名", "通所 / 退所", "作業内容", "利用者の様子"));
        foreach (var d in items) g.Rows.Add(Row(false, d.ClientName, $"{d.CheckIn ?? "—"} / {d.CheckOut ?? "—"}", d.Work, d.Notes));
        doc.Blocks.Add(t);

        doc.Blocks.Add(new Paragraph(new Run("終礼メモ")) { FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 16, 0, 4) });
        doc.Blocks.Add(new Paragraph(new Run(memo.Text == "" ? "(なし)" : memo.Text)) { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), Padding = new Thickness(6) });
        doc.Blocks.Add(new Paragraph(new Run($"※個人情報を含みます。取り扱いに注意し、不要になったら裁断してください。  出力者: {staff}  出力日時: {DateTime.Now:yyyy/M/d HH:mm}")) { FontSize = 9, Foreground = Brushes.DimGray, Margin = new Thickness(0, 14, 0, 0) });
        return doc;
    }

    private static TableRow Row(bool head, params string[] cells)
    {
        var r = new TableRow { Background = head ? Brushes.Gainsboro : null };
        foreach (var c in cells)
            r.Cells.Add(new TableCell(new Paragraph(new Run(c)) { FontWeight = head ? FontWeights.Bold : FontWeights.Normal }) { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), Padding = new Thickness(4) });
        return r;
    }
}
