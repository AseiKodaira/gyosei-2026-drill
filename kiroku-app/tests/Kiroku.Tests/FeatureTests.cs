using Kiroku.Core;
using Kiroku.Client;

namespace Kiroku.Tests;

public class FeatureTests : IAsyncLifetime
{
    private readonly TestServer _s = new();
    private KirokuApi _admin = null!;
    public async Task InitializeAsync() { await _s.StartAsync(); (_admin, _) = await _s.SetupAdmin(); }
    public async Task DisposeAsync() => await _s.DisposeAsync();

    private static string Today => DateTime.Today.ToString("yyyy-MM-dd");

    [Fact] // 受入1・2: 初めての氏名を登録でき、次回はプルダウンから選べる
    public async Task NewName_AutoRegistered_ThenSelectable()
    {
        var r = await _admin.SaveRecord(new(null, "山田　太郎", Today, "9:30", null, null, null, 0));
        Assert.Equal("山田 太郎", r.ClientName);
        var list = await _admin.Clients();
        Assert.Single(list);
    }

    [Fact]
    public async Task NameSearch_Substring_AndDropdown()
    {
        await _admin.SaveRecord(new(null, "山田 太郎", Today, null, null, null, null, 0));
        await _admin.SaveRecord(new(null, "佐藤 花子", Today, null, null, null, null, 0));
        Assert.Equal(2, (await _admin.Clients()).Count);
        Assert.Single(await _admin.Clients("山田"));
        Assert.Single(await _admin.Clients("山田太郎")); // 空白の有無を無視
        Assert.Single(await _admin.Clients("花"));
    }

    [Fact] // 受入3・4: 通所時間だけで保存→夕方に追記
    public async Task CheckInOnly_ThenEveningAppend()
    {
        var m = await _admin.SaveRecord(new(null, "山田 太郎", Today, "0930", null, "", "", 0));
        Assert.Equal("09:30", m.CheckIn); Assert.Null(m.CheckOut); Assert.Equal(1, m.Version);
        var e = await _admin.SaveRecord(new(m.ClientId, null, Today, "09:30", "15:45", "袋詰め作業", "落ち着いて作業できた", m.Version));
        Assert.Equal(2, e.Version); Assert.Equal(m.Id, e.Id); // 新規重複ではなく同じ記録の編集
        Assert.Single(await _admin.Records(Today));
        var hist = await _admin.RecordHistory(e.Id);
        Assert.Single(hist); Assert.Equal("", hist[0].Work); // 修正前の内容が残る
        Assert.Equal("管理者 花子", hist[0].ReplacedByName);
    }

    [Fact] // 過去の記録がコピーされない/日付別
    public async Task NewDay_DoesNotCopyPreviousContent()
    {
        var a = await _admin.SaveRecord(new(null, "山田 太郎", "2026-04-01", "9:00", "16:00", "検品", "元気", 0));
        Assert.Null(await _admin.Record(a.ClientId, "2026-04-02"));
        var b = await _admin.SaveRecord(new(a.ClientId, null, "2026-04-02", "9:10", null, null, null, 0));
        Assert.Equal("", b.Work); Assert.Equal("", b.Notes);
    }

    [Fact]
    public async Task TimeValidation_WarnsOnContradiction_AndRejectsGarbage()
    {
        var ex = await Assert.ThrowsAsync<KirokuException>(() => _admin.SaveRecord(new(null, "山田 太郎", Today, "16:00", "09:00", null, null, 0)));
        Assert.Equal("time_warning", ex.Code);
        var ok = await _admin.SaveRecord(new(null, "山田 太郎", Today, "16:00", "09:00", null, null, 0, ConfirmTimeWarning: true));
        Assert.Equal("09:00", ok.CheckOut);
        var bad = await Assert.ThrowsAsync<KirokuException>(() => _admin.SaveRecord(new(null, "鈴木 一郎", Today, "25:99", null, null, null, 0)));
        Assert.Equal("bad_time", bad.Code);
        Assert.Single(await _admin.Clients()); // 失敗した保存で利用者だけ作られない
    }

    [Fact] // 氏名・日付のみ必須
    public async Task RequiredFields()
    {
        var e1 = await Assert.ThrowsAsync<KirokuException>(() => _admin.SaveRecord(new(null, "  ", Today, null, null, null, null, 0)));
        Assert.Equal("name_required", e1.Code);
        var e2 = await Assert.ThrowsAsync<KirokuException>(() => _admin.SaveRecord(new(null, "山田", "", null, null, null, null, 0)));
        Assert.Equal("bad_date", e2.Code);
    }

    [Fact] // 同姓同名: 確認を挟み、承認すれば内部IDで別人として登録
    public async Task DuplicateName_RequiresConfirmation_ThenSeparateIds()
    {
        var a = await _admin.SaveRecord(new(null, "山田 太郎", Today, null, null, null, null, 0));
        var ex = await Assert.ThrowsAsync<KirokuException>(() => _admin.SaveRecord(new(null, "山田太郎", Today, null, null, null, null, 0)));
        Assert.Equal("duplicate_name", ex.Code);
        var b = await _admin.SaveRecord(new(null, "山田太郎", Today, null, null, null, null, 0, ConfirmDuplicateName: true));
        Assert.NotEqual(a.ClientId, b.ClientId);
        Assert.Equal(2, (await _admin.Records(Today)).Count);
    }

    [Fact] // 氏名の誤字修正は管理者のみ
    public async Task Rename_AdminOnly()
    {
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        var r = await staff.SaveRecord(new(null, "山田 太朗", Today, null, null, null, null, 0));
        var denied = await Assert.ThrowsAsync<KirokuException>(() => staff.RenameClient(r.ClientId, "山田 太郎"));
        Assert.Equal(403, denied.Status);
        await _admin.RenameClient(r.ClientId, "山田 太郎");
        Assert.Equal("山田 太郎", (await staff.Clients()).Single().Name);
        Assert.Equal("山田 太郎", (await staff.Records(Today)).Single().ClientName);
    }

    [Fact]
    public async Task WorkSuggestions_FromPastEntries_NoMaster()
    {
        await _admin.SaveRecord(new(null, "山田 太郎", "2026-04-01", null, null, "袋詰め作業", null, 0));
        await _admin.SaveRecord(new(null, "佐藤 花子", "2026-04-01", null, null, "袋詰め作業", null, 0));
        await _admin.SaveRecord(new(null, "鈴木 一郎", "2026-04-01", null, null, "清掃作業", null, 0));
        Assert.Equal("袋詰め作業", (await _admin.WorkSuggestions("")).First());
        Assert.Equal(["清掃作業"], await _admin.WorkSuggestions("清掃"));
    }

    [Fact] // 受入7: 終礼メモ共有 + 履歴 + 競合
    public async Task Memo_Shared_History_AndConflict()
    {
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        var m1 = await _admin.SaveMemo(new(Today, "明日は検品が多い", 0));
        Assert.Equal("明日は検品が多い", (await staff.Memo(Today)).Text);
        Assert.Equal("管理者 花子", (await staff.Memo(Today)).UpdatedByName);
        var m2 = await staff.SaveMemo(new(Today, "明日は検品が多い。体調不良者あり", m1.Version));
        // 管理者は古い版(m1)のまま保存しようとする → 他職員の内容を消さずに拒否
        var ex = await Assert.ThrowsAsync<KirokuException>(() => _admin.SaveMemo(new(Today, "別の内容で上書き", m1.Version)));
        Assert.Equal("conflict", ex.Code);
        Assert.Equal("明日は検品が多い。体調不良者あり", (await _admin.Memo(Today)).Text);
        Assert.Equal(m2.Version, (await _admin.Memo(Today)).Version);
        Assert.Equal("明日は検品が多い", (await _admin.MemoHistory(Today)).Single().Text);
        Assert.Equal("", (await _admin.Memo("2026-01-05")).Text); // 日付を替えると別のメモ
    }

    [Fact] // 受入9: 同時編集で記録が消えない
    public async Task ConcurrentRecordEdit_NoSilentOverwrite()
    {
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        var r = await _admin.SaveRecord(new(null, "山田 太郎", Today, "9:00", null, null, null, 0));
        var results = await Task.WhenAll(
            Try(() => _admin.SaveRecord(new(r.ClientId, null, Today, "9:00", "16:00", "検品", "A職員の記録", r.Version))),
            Try(() => staff.SaveRecord(new(r.ClientId, null, Today, "9:00", "16:30", "袋詰め", "B職員の記録", r.Version))));
        Assert.Equal(1, results.Count(x => x == null));         // 1人だけ成功
        Assert.Equal(1, results.Count(x => x == "conflict"));   // もう1人は競合として検出
        var final = await _admin.Record(r.ClientId, Today);
        Assert.Contains(final!.Notes, new[] { "A職員の記録", "B職員の記録" });
        Assert.Equal(2, final.Version);
        Assert.Single(await _admin.RecordHistory(final.Id)); // 更新前の履歴も保持
    }

    [Fact] // 競合時に相手の最新内容が返り、それを基に再保存できる
    public async Task Conflict_ReturnsCurrent_AndResaveSucceeds()
    {
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        var r = await _admin.SaveRecord(new(null, "山田 太郎", Today, "9:00", null, null, null, 0));
        await staff.SaveRecord(new(r.ClientId, null, Today, "9:00", null, "検品", "", r.Version));
        var ex = await Assert.ThrowsAsync<KirokuException>(() => _admin.SaveRecord(new(r.ClientId, null, Today, "9:05", null, null, null, r.Version)));
        Assert.Equal("conflict", ex.Code);
        var cur = System.Text.Json.JsonSerializer.Deserialize<RecordDetail>(((System.Text.Json.JsonElement)ex.Detail!).GetRawText(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("検品", cur.Work);
        var ok = await _admin.SaveRecord(new(r.ClientId, null, Today, "9:05", null, cur.Work, cur.Notes, cur.Version));
        Assert.Equal("検品", ok.Work);
    }

    [Fact] // 新規作成の同時衝突(同じ利用者・同じ日に2人が初回保存)
    public async Task ConcurrentCreate_SameClientAndDay_Detected()
    {
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        var c = (await _admin.SaveRecord(new(null, "山田 太郎", "2026-01-01", null, null, null, null, 0))).ClientId;
        var results = await Task.WhenAll(
            Try(() => _admin.SaveRecord(new(c, null, Today, "9:00", null, null, null, 0))),
            Try(() => staff.SaveRecord(new(c, null, Today, "9:30", null, null, null, 0))));
        Assert.Equal(1, results.Count(x => x == "conflict"));
        Assert.Single((await _admin.Records(Today)));
    }

    [Fact] // 受入5: 保存が他の端末に反映(変更検知の revision も進む)
    public async Task SavePropagates_ToOtherDevices_AndPulseAdvances()
    {
        var staff = await _s.NewStaffApi(_admin, "sato", "佐藤 職員");
        var before = (await staff.Pulse()).Revision;
        await _admin.SaveRecord(new(null, "山田 太郎", Today, "9:00", null, null, null, 0));
        Assert.True((await staff.Pulse()).Revision > before);
        Assert.Single(await staff.Records(Today));
        await staff.SaveRecord(new(null, "佐藤 花子", Today, "9:10", null, null, null, 0));
        Assert.Equal(2, (await _admin.Records(Today)).Count);
    }

    [Fact] // 過去の記録: 検索・閲覧・修正
    public async Task PastRecords_Search_View_Edit()
    {
        var yamada = (await _admin.SaveRecord(new(null, "山田 太郎", "2026-03-01", "9:00", "16:00", "検品", "良好", 0))).ClientId;
        await _admin.SaveRecord(new(yamada, null, "2026-03-05", "9:00", "16:00", "袋詰め", "疲れ気味", 0));
        await _admin.SaveRecord(new(null, "佐藤 花子", "2026-03-05", "9:00", null, null, null, 0));
        Assert.Equal(2, (await _admin.Search("山田", null, null)).Count);
        Assert.Equal(2, (await _admin.Search(null, "2026-03-05", "2026-03-05")).Count);
        var one = (await _admin.Search("山田", "2026-03-05", "2026-03-05")).Single();
        var d = await _admin.GetRecord(one.RecordId);
        var upd = await _admin.SaveRecord(new(d.ClientId, null, d.Date, d.CheckIn, d.CheckOut, d.Work, "疲れ気味だったが午後は回復", d.Version));
        Assert.Equal(2, upd.Version);
    }

    [Fact]
    public async Task PrintPermission_Enforced_AndAudited()
    {
        var plain = await _s.NewStaffApi(_admin, "sato", "佐藤 職員", canPrint: false);
        var ex = await Assert.ThrowsAsync<KirokuException>(() => plain.AuthorizePrint("daily", Today));
        Assert.Equal("no_print_permission", ex.Code);
        var printer = await _s.NewStaffApi(_admin, "suzuki", "鈴木 職員", canPrint: true);
        await printer.AuthorizePrint("daily", Today);
        Assert.Contains((await _admin.Audit()), a => a.Action == "print" && a.StaffName == "鈴木 職員");
    }

    private static async Task<string?> Try(Func<Task> f)
    {
        try { await f(); return null; } catch (KirokuException e) { return e.Code; }
    }

    [Fact]
    public void TimeText_Normalization()
    {
        Assert.Equal("09:30", TimeText.Normalize("9:30", "x"));
        Assert.Equal("09:30", TimeText.Normalize("０９：３０", "x"));
        Assert.Equal("16:05", TimeText.Normalize("1605", "x"));
        Assert.Equal("09:00", TimeText.Normalize("9", "x"));
        Assert.Equal("09:30", TimeText.Normalize("9時30分", "x"));
        Assert.Null(TimeText.Normalize("  ", "x"));
        Assert.Throws<KirokuException>(() => TimeText.Normalize("24:00", "x"));
    }
}
