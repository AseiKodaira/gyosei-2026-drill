using Kiroku.Client;
using Kiroku.Core;

namespace Kiroku.Tests;

public class CopyTests : IAsyncLifetime
{
    private readonly TestServer _s = new();
    private KirokuApi _api = null!;
    public async Task InitializeAsync() { await _s.StartAsync(); (_api, _) = await _s.SetupAdmin(); }
    public async Task DisposeAsync() => await _s.DisposeAsync();

    private const string Work = "鍵部品の組立\n検品(10個/箱)→袋詰め\n※「重要」: 〒・&・<>・\"引用\"・😀";
    private const string Notes = "体調良好。\n\n午後は集中が続いた。\n職員の声かけで休憩 #2 を取った。";

    [Fact] // 完成条件1〜3: 7項目を個別に取り出せ、ラベルや他項目が混ざらず、日本語・改行・記号がそのまま
    public async Task EachItem_IsExactValue_WithoutLabelsOrOtherFields()
    {
        var saved = await _api.SaveRecord(new(null, "山田 太郎", "2026-10-05", "9:30", "16:05", Work, Notes, 0));
        var memo = await _api.SaveMemo(new("2026-10-05", "終礼: 明日の検品は\n30箱です。", 0));
        var r = (await _api.GetRecord(saved.Id));
        var m = await _api.Memo("2026-10-05");

        Assert.Equal("山田 太郎", CopyValues.Name(r));
        Assert.Equal("2026/10/05", CopyValues.Date(r));
        Assert.Equal("09:30", CopyValues.CheckIn(r));
        Assert.Equal("16:05", CopyValues.CheckOut(r));
        Assert.Equal(Work, CopyValues.Work(r));
        Assert.Equal(Notes, CopyValues.Notes(r));
        Assert.Equal("終礼: 明日の検品は\n30箱です。", CopyValues.Memo(m));

        var all = new[] { CopyValues.Name(r), CopyValues.Date(r), CopyValues.CheckIn(r), CopyValues.CheckOut(r), CopyValues.Work(r), CopyValues.Notes(r), CopyValues.Memo(m) };
        Assert.Equal(7, all.Distinct().Count());
        foreach (var label in new[] { "氏名", "日付", "通所", "退所", "作業内容：", "様子：", "終礼メモ" })
            Assert.DoesNotContain(all, v => v!.StartsWith(label));
        Assert.DoesNotContain("山田", CopyValues.Work(r)!);          // 別項目の混入なし
        Assert.DoesNotContain("09:30", CopyValues.Notes(r)!);
    }

    [Fact]
    public void Clipboard_Newlines_AreNormalizedToCrLf_AndNothingElseChanges()
    {
        Assert.Equal("あ\r\nい\r\n\r\nう", CopyValues.ForClipboard("あ\nい\n\nう"));
        Assert.Equal("あ\r\nい", CopyValues.ForClipboard("あ\r\nい"));
        Assert.Equal("〒&<>\"😀", CopyValues.ForClipboard("〒&<>\"😀"));
    }

    [Fact] // 空欄はコピー不可(null)
    public async Task Blank_IsNull_SoButtonIsDisabled()
    {
        var saved = await _api.SaveRecord(new(null, "山田 太郎", "2026-10-05", "9:30", null, null, "   ", 0));
        var r = await _api.GetRecord(saved.Id);
        Assert.Null(CopyValues.CheckOut(r)); Assert.Null(CopyValues.Work(r)); Assert.Null(CopyValues.Notes(r));
        Assert.Null(CopyValues.Memo(await _api.Memo("2026-10-05")));
        Assert.Null(CopyValues.Name(null)); Assert.Null(CopyValues.Date(null));
    }

    [Fact] // 完成条件5: コピー(値の取り出し)は記録を変更しない
    public async Task Copying_DoesNotChangeStoredRecord()
    {
        var saved = await _api.SaveRecord(new(null, "山田 太郎", "2026-10-05", "9:30", "16:05", Work, Notes, 0));
        var before = await _api.GetRecord(saved.Id);
        Assert.Empty(await _api.RecordHistory(saved.Id));                       // 履歴は空(履歴の閲覧自体は監査に残るので先に実施)
        var rev = (await _api.Pulse()).Revision;
        var auditCount = (await _api.Audit(2000)).Count;
        for (var i = 0; i < 3; i++) { _ = CopyValues.Name(before); _ = CopyValues.Work(before); _ = CopyValues.ForClipboard(Notes); }
        var after = await _api.GetRecord(saved.Id);
        Assert.Equal(before, after);
        Assert.Equal(1, after.Version);
        Assert.Equal(rev, (await _api.Pulse()).Revision);                       // 変更なし
        Assert.Equal(auditCount, (await _api.Audit(2000)).Count);               // 親機に何も送られない
    }

    [Fact] // 完成条件6: コピー処理のソースに通信・ログ・ファイル書き込みがない
    public void CopyCode_HasNoNetworkLoggingOrFileOutput()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "Kiroku.App"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        foreach (var f in new[] { "src/Kiroku.App/CopyService.cs", "src/Kiroku.Client/CopyValues.cs" })
        {
            var code = File.ReadAllText(Path.Combine(dir!, f));
            foreach (var bad in new[] { "HttpClient", "Socket", "WebRequest", "Log", "Console.", "File.Write", "File.Append", "Debug.", "Trace.", "KirokuApi" })
                Assert.DoesNotContain(bad, code);
        }
    }
}
