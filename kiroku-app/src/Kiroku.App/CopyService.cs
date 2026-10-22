using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kiroku.Client;

namespace Kiroku.App;

/// <summary>
/// knowbe転記用のコピー。Windows標準クリップボードにだけ書く(通信しない・ログに内容を残さない)。
/// クリップボード履歴・他端末との同期の対象外になるよう Windows 標準の除外フォーマットを付け、
/// 60秒後に(まだ自分がコピーした内容のままなら)クリップボードを空にする。
/// ※他のアプリはクリップボードを読めるため、秘密保持を保証するものではない。
/// </summary>
internal static class CopyService
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(60);
    private static DispatcherTimer? _clear;
    private static string? _lastCopied;

    /// <summary>成功なら null、失敗なら利用者向けの原因メッセージ。</summary>
    public static string? Copy(string text)
    {
        var data = new DataObject();
        data.SetText(CopyValues.ForClipboard(text), TextDataFormat.UnicodeText);
        // Windows標準: クリップボード履歴(Win+V)とデバイス間同期から除外
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));

        for (var i = 0; i < 6; i++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                ScheduleClear(text);
                return null;
            }
            catch (COMException) { Thread.Sleep(60); }      // 他のアプリがクリップボードを使用中のことがある
            catch (ExternalException) { Thread.Sleep(60); }
        }
        return "コピーできませんでした。他のアプリがクリップボードを使っている可能性があります。少し待ってもう一度押してください。";
    }

    private static void ScheduleClear(string text)
    {
        _lastCopied = CopyValues.ForClipboard(text);
        _clear?.Stop();
        _clear = new DispatcherTimer { Interval = KeepFor };
        _clear.Tick += (_, _) =>
        {
            _clear!.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == _lastCopied) Clipboard.Clear(); }
            catch (Exception) { /* 取得できなければ何もしない */ }
            _lastCopied = null;
        };
        _clear.Start();
    }

    /// <summary>項目名の横に置く小さなコピーボタン。</summary>
    public static Button Button(Func<string?> savedValue, Func<bool> isUnsaved, Action<string?> report /* null=成功, それ以外=失敗の原因 */, out Action refresh)
    {
        var b = new Button
        {
            Content = "📋 コピー", FontSize = 12, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 2), MinHeight = 24,
            Background = Ui.White, BorderBrush = Ui.Line, Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
        };
        Action update = () =>
        {
            var unsaved = isUnsaved();
            var v = unsaved ? null : savedValue();
            b.IsEnabled = v != null;
            b.ToolTip = unsaved ? "未保存の変更があります。保存してからコピーしてください" : v == null ? "コピーできる内容がありません" : "この項目の内容だけをコピーします(knowbeに貼り付けられます)";
        };
        b.Click += (_, _) =>
        {
            if (isUnsaved()) { report("未保存の変更があるため、コピーしませんでした。先に保存してください。"); update(); return; }
            var v = savedValue();
            if (v == null) { update(); return; }
            report(Copy(v));
        };
        refresh = update;
        return b;
    }
}
