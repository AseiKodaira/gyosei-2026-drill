using Kiroku.Core;

namespace Kiroku.Server;

public sealed class BackupHealth { public string? LastError; public DateTimeOffset? LastErrorAt; }

/// <summary>毎日の自動バックアップ。起動時と5分ごとに「今日分があるか」を確認する。</summary>
public sealed class BackupWorker(BackupService backups, Func<ServerConfig> cfg, BackupHealth health, ILogger<BackupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var st = backups.Status();
                var last = st.LastLocalAt == null ? (DateTime?)null : DateTime.Parse(st.LastLocalAt);
                var due = last == null || (last.Value.Date < now.Date && now.Hour >= cfg().BackupHour) || now - last.Value > TimeSpan.FromHours(30);
                // 別媒体が最近つながったときは取り直して持ち出し用の最新を置く
                var extStale = !string.IsNullOrWhiteSpace(cfg().ExternalBackupPath) && st.ExternalReachable
                               && (st.LastExternalAt == null || DateTime.Parse(st.LastExternalAt).Date < now.Date) && now.Hour >= cfg().BackupHour;
                if (due || extStale)
                {
                    backups.RunNow(Actor.System);
                    health.LastError = null;
                }
            }
            catch (KirokuException e) { health.LastError = e.Message; health.LastErrorAt = DateTimeOffset.Now; log.LogWarning("自動バックアップ失敗: {Code}", e.Code); }
            catch (Exception e) { health.LastError = "自動バックアップで想定外のエラーが起きました。"; health.LastErrorAt = DateTimeOffset.Now; log.LogError("自動バックアップ失敗: {Type}", e.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { }
        }
    }
}
