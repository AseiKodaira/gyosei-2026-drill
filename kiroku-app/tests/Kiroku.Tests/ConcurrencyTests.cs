using Kiroku.Client;
using Kiroku.Core;

namespace Kiroku.Tests;

public class ConcurrencyTests : IAsyncLifetime
{
    private readonly TestServer _s = new();
    public async Task InitializeAsync() => await _s.StartAsync();
    public async Task DisposeAsync() => await _s.DisposeAsync();

    [Fact] // 受入14(模擬): 5台が同時に入力しても、1件も失われず、競合は必ず検出される
    public async Task FiveDevices_Concurrent_NoLostWrites()
    {
        var (admin, _) = await _s.SetupAdmin();
        var apis = new List<KirokuApi> { admin };
        for (var i = 1; i < 5; i++) apis.Add(await _s.NewStaffApi(admin, "staff" + i, "職員" + i));
        var day = "2026-06-01";

        // 共有の1件 + 各端末が自分の利用者を10人ずつ登録
        var shared = await admin.SaveRecord(new(null, "共有 太郎", day, "9:00", null, "", "", 0));
        var success = new int[5]; var conflicts = new int[5];
        await Task.WhenAll(apis.Select((api, n) => Task.Run(async () =>
        {
            for (var k = 0; k < 10; k++)
            {
                await api.SaveRecord(new(null, $"利用者{n}-{k}", day, "9:00", null, $"作業{n}-{k}", "", 0));
                // 共有レコードを楽観ロックで更新(競合したら最新を取り直して再試行)
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    var cur = (await api.Record(shared.ClientId, day))!;
                    try
                    {
                        await api.SaveRecord(new(shared.ClientId, null, day, "9:00", null, cur.Work, cur.Notes + $"[{n}-{k}]", cur.Version));
                        success[n]++; break;
                    }
                    catch (KirokuException e) when (e.Code == "conflict") { conflicts[n]++; }
                }
            }
        })));

        Assert.Equal(50, success.Sum());                          // 50回の更新すべてが(再試行で)反映
        var final = (await admin.Record(shared.ClientId, day))!;
        for (var n = 0; n < 5; n++) for (var k = 0; k < 10; k++) Assert.Contains($"[{n}-{k}]", final.Notes); // 誰の追記も消えていない
        Assert.Equal(51, final.Version);                          // 作成1 + 更新50
        Assert.Equal(50, (await admin.RecordHistory(final.Id)).Count); // 更新前の履歴が全件
        Assert.Equal(51, (await admin.Records(day)).Count);       // 共有1 + 50人
        Assert.True(conflicts.Sum() > 0, "競合が一度も起きなかった(試験が並行になっていない可能性)");
        Assert.True(_s.Store.IntegrityOk());
        Assert.True(_s.Store.VerifyAudit().Ok);
    }

    [Fact] // 終礼メモを5台が同時に追記しても消えない
    public async Task Memo_ConcurrentAppend_NoLoss()
    {
        var (admin, _) = await _s.SetupAdmin();
        var apis = new List<KirokuApi> { admin };
        for (var i = 1; i < 5; i++) apis.Add(await _s.NewStaffApi(admin, "staff" + i, "職員" + i));
        var day = "2026-06-02";
        await Task.WhenAll(apis.Select((api, n) => Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var m = await api.Memo(day);
                try { await api.SaveMemo(new(day, m.Text + $"行{n}\n", m.Version)); return; }
                catch (KirokuException e) when (e.Code == "conflict") { }
            }
            throw new Exception("再試行上限");
        })));
        var text = (await admin.Memo(day)).Text;
        for (var n = 0; n < 5; n++) Assert.Contains($"行{n}", text);
    }
}
