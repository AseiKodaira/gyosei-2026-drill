using System.Globalization;

namespace Kiroku.Core;

/// <summary>暗号化バックアップの作成・検証・世代管理・復元。外部送信は一切しない。</summary>
public sealed class BackupService(DataPaths paths, KirokuStore store, KeyStore keys, Func<ServerConfig> cfg)
{
    private readonly object _lock = new();
    private const string Ext = ".krbk";

    private static string StampName(DateTimeOffset t) => $"kiroku-{t:yyyyMMdd-HHmmss}{Ext}";

    public static bool IsReachable(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            if (!Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, ".kiroku-probe-" + Rand.Id());
            File.WriteAllText(probe, "x"); File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>バックアップを作る。ローカル作成→検証→外部コピー→外部側も検証→世代整理。</summary>
    public BackupStatus RunNow(Actor actor)
    {
        lock (_lock)
        {
            var wrap = keys.LoadRecoveryWrap() ?? throw new KirokuException(500, "no_recovery", "復旧キーの情報がありません。管理者画面から復旧キーを再発行してください。");
            var need = Math.Max(store.DbSize * 4, 50L << 20);
            var free = FreeBytes(paths.Backups);
            if (free >= 0 && free < need)
                throw new KirokuException(507, "disk_full", $"バックアップ先の空き容量が足りません(必要の目安 {need >> 20}MB / 空き {free >> 20}MB)。不要なファイルを消すか、別のドライブを使ってください。");

            paths.Ensure();
            var name = StampName(store.Clock());
            var snap = Path.Combine(paths.Tmp, "snap-" + Rand.Id() + ".db");
            var local = Path.Combine(paths.Backups, name);
            try
            {
                store.SnapshotTo(snap);
                BackupCrypto.Encrypt(snap, local, store.DbKey, wrap);
            }
            finally { SafeDelete(snap); }

            if (!VerifyFile(local))
            {
                SafeDelete(local);
                store.WriteAudit(actor, "backup_failed", name, "作成直後の検証に失敗");
                throw new KirokuException(500, "backup_verify", "バックアップを作りましたが検証に失敗したため破棄しました。ディスクの異常が疑われます。");
            }

            var c = cfg();
            var extNote = "外部なし";
            if (!string.IsNullOrWhiteSpace(c.ExternalBackupPath))
            {
                if (IsReachable(c.ExternalBackupPath))
                {
                    var dest = Path.Combine(c.ExternalBackupPath, name);
                    File.Copy(local, dest, true);
                    extNote = VerifyFile(dest) ? "外部OK" : "外部コピー検証NG";
                    if (extNote != "外部OK") SafeDelete(dest);
                    Prune(c.ExternalBackupPath, c.KeepExternal);
                }
                else extNote = "外部に接続できません";
            }
            Prune(paths.Backups, c.KeepLocal);
            store.WriteAudit(actor, "backup_ok", name, extNote);
            return Status();
        }
    }

    /// <summary>バックアップを稼働中のDB鍵で復号し、開いて整合性検査まで行う。</summary>
    public bool VerifyFile(string path)
    {
        var tmp = Path.Combine(paths.Tmp, "verify-" + Rand.Id() + ".db");
        try
        {
            Directory.CreateDirectory(paths.Tmp);
            BackupCrypto.DecryptWithDbKey(path, tmp, store.DbKey);
            return KirokuStore.VerifyFile(tmp, store.DbKey);
        }
        catch (KirokuException) { return false; }
        catch (IOException) { return false; }
        finally { SafeDelete(tmp); foreach (var e in new[] { "-wal", "-shm" }) SafeDelete(tmp + e); }
    }

    /// <summary>すべてのバックアップを検証して結果を返す(管理者の「確認」ボタン用)。</summary>
    public IReadOnlyList<(BackupFile File, bool Ok)> VerifyAll() =>
        List().Select(f => (f, VerifyFile(FullPath(f)))).ToList();

    public string FullPath(BackupFile f) =>
        Path.Combine(f.Location == "local" ? paths.Backups : cfg().ExternalBackupPath ?? "", f.Name);

    public IReadOnlyList<BackupFile> List()
    {
        var l = new List<BackupFile>();
        void Add(string? dir, string loc)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "kiroku-*" + Ext))
                {
                    var fi = new FileInfo(f);
                    l.Add(new BackupFile(fi.Name, fi.Length, fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), loc));
                }
            }
            catch (IOException) { }
        }
        Add(paths.Backups, "local"); Add(cfg().ExternalBackupPath, "external");
        return l.OrderByDescending(f => f.Name).ThenBy(f => f.Location).ToList();
    }

    public BackupStatus Status()
    {
        var c = cfg();
        var files = List();
        var reach = IsReachable(c.ExternalBackupPath);
        var warn = new List<string>();
        DateTimeOffset? Last(string loc) => files.Where(f => f.Location == loc).Select(f => ParseName(f.Name)).Where(x => x != null).OrderByDescending(x => x).FirstOrDefault();
        var lastLocal = Last("local"); var lastExt = Last("external");
        var now = store.Clock();

        if (string.IsNullOrWhiteSpace(c.ExternalBackupPath))
            warn.Add("【重要】USBメモリ・外付けHDDなど別の媒体へのバックアップが未設定です。親機のディスクだけに置いたバックアップは、親機が故障すると一緒に失われます。");
        else if (!reach)
            warn.Add($"バックアップ先「{c.ExternalBackupPath}」に接続できません。USBメモリ等が挿さっているか確認してください。");
        else if (lastExt == null || now - lastExt > TimeSpan.FromDays(3))
            warn.Add("別媒体への最新バックアップが3日以上前です。「今すぐバックアップ」を実行してください。");
        if (!string.IsNullOrWhiteSpace(c.ExternalBackupPath) && SameDrive(c.ExternalBackupPath, paths.Root))
            warn.Add("バックアップ先が親機のデータと同じドライブです。別の媒体(USBメモリ等)を指定してください。");
        if (lastLocal == null || now - lastLocal > TimeSpan.FromHours(36))
            warn.Add("親機内のバックアップが1日半以上作られていません。");
        var free = FreeBytes(paths.Backups);
        if (free >= 0 && free < Math.Max(store.DbSize * 4, 50L << 20)) warn.Add("親機の空き容量が少なくなっています。バックアップできなくなる前に整理してください。");
        if (keys.LoadRecoveryWrap() == null) warn.Add("復旧キーの情報がありません。復旧キーを再発行してください。");

        return new BackupStatus(lastLocal?.ToString("yyyy-MM-dd HH:mm"), lastExt?.ToString("yyyy-MM-dd HH:mm"), c.ExternalBackupPath, reach, free, store.DbSize, warn, files);
    }

    private static DateTimeOffset? ParseName(string name)
    {
        var s = name.Replace("kiroku-", "").Replace(Ext, "");
        return DateTime.TryParseExact(s, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? new DateTimeOffset(d) : null;
    }

    private static bool SameDrive(string a, string b)
    {
        try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows(); }
        catch { return false; }
    }

    private static long FreeBytes(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; } catch { return -1; }
    }

    private static void Prune(string dir, int keep)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir, "kiroku-*" + Ext).OrderByDescending(x => x).Skip(Math.Max(keep, 3))) SafeDelete(f);
        }
        catch (IOException) { }
    }

    private static void SafeDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { /* 次回の起動時に Tmp を掃除 */ } }

    /// <summary>復元。事前に現在のデータの安全バックアップを取り、復旧キーで復号して入れ替える。</summary>
    public void Restore(Actor actor, string location, string fileName, string recoveryKey)
    {
        lock (_lock)
        {
            if (!RecoveryKey.LooksValid(recoveryKey))
                throw new KirokuException(400, "recovery_key", "復旧キーの形式が正しくありません(英数字40文字)。");
            var dir = location == "external" ? cfg().ExternalBackupPath : paths.Backups;
            var file = Path.Combine(dir ?? "", Path.GetFileName(fileName));
            if (!File.Exists(file)) throw new KirokuException(404, "no_backup", "指定のバックアップが見つかりません。");
            RestoreFromFile(actor, file, recoveryKey, takeSafetyBackup: true);
        }
    }

    public void RestoreFromFile(Actor actor, string file, string recoveryKey, bool takeSafetyBackup)
    {
        var tmp = Path.Combine(paths.Tmp, "restore-" + Rand.Id() + ".db");
        Directory.CreateDirectory(paths.Tmp);
        try
        {
            var key = BackupCrypto.Decrypt(file, tmp, recoveryKey);
            if (!KirokuStore.VerifyFile(tmp, key)) throw new KirokuException(400, "restore_corrupt", "バックアップの中身が壊れています。別の世代で試してください。");
            if (takeSafetyBackup) { try { RunNow(actor); } catch (KirokuException) { /* 容量不足等でも復元自体は止めない */ } }
            store.ReplaceWith(tmp, key);
            keys.StoreDbKey(key);
            keys.StoreRecoveryWrap(BackupCrypto.ReadWrap(file));
            store.WriteAudit(actor, "restore", Path.GetFileName(file), "バックアップから復元");
        }
        finally { SafeDelete(tmp); foreach (var e in new[] { "-wal", "-shm" }) SafeDelete(tmp + e); }
    }

    /// <summary>復旧キーの再発行(新しいキーでDB鍵を包み直す)。旧キーは無効になる。</summary>
    public string ReissueRecoveryKey(Actor actor)
    {
        var rk = RecoveryKey.Generate();
        keys.StoreRecoveryWrap(RecoveryWrap.Create(store.DbKey, rk));
        store.WriteAudit(actor, "recovery_key_reissue", "", "古いバックアップは旧キーのまま。再発行後に新しいバックアップを作成してください");
        return rk;
    }
}
