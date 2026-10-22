using System.Text.RegularExpressions;
using Kiroku.Core;

namespace Kiroku.Client;

/// <summary>
/// knowbe転記用に「保存済みの記録」から各項目の値だけを取り出す。項目名(ラベル)は付けない。
/// 空欄は null(コピーボタン無効)。通信・ログ出力・記録の変更は一切しない。
/// </summary>
public static class CopyValues
{
    public static string? Name(RecordDetail? r) => Blank(r?.ClientName);
    public static string? Date(RecordDetail? r) => r == null ? null : DateTime.ParseExact(r.Date, "yyyy-MM-dd", null).ToString("yyyy/MM/dd");
    public static string? CheckIn(RecordDetail? r) => Blank(r?.CheckIn);
    public static string? CheckOut(RecordDetail? r) => Blank(r?.CheckOut);
    public static string? Work(RecordDetail? r) => Blank(r?.Work);
    public static string? Notes(RecordDetail? r) => Blank(r?.Notes);
    public static string? Memo(MemoDetail? m) => Blank(m?.Text);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Windowsの通常のテキスト欄向けに改行を CRLF にそろえる(それ以外は加工しない)。</summary>
    public static string ForClipboard(string s) => Regex.Replace(s, @"\r\n|\r|\n", "\r\n");
}
