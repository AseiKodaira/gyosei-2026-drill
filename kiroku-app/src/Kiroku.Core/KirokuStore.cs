using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kiroku.Core;

public sealed record Actor(string StaffId, string StaffName, string DeviceId)
{
    public static readonly Actor System = new("system", "システム", "-");
}

/// <summary>暗号化SQLite(SQLCipher)の唯一の操作窓口。親機のサーバープロセス内にのみ存在する。</summary>
public sealed partial class KirokuStore : IDisposable
{
    private readonly object _gate = new();
    private SqliteConnection _db;
    private byte[] _key;
    public string Path { get; }
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    static KirokuStore() => SQLitePCL.Batteries_V2.Init();

    public KirokuStore(string path, byte[] key)
    {
        Path = path; _key = key;
        _db = Open(path, key);
        Migrate();
    }

    private static SqliteConnection Open(string path, byte[] key, bool create = true)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        Exec(c, null, $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\"");
        try { Scalar(c, null, "SELECT count(*) FROM sqlite_master"); }
        catch (SqliteException)
        {
            c.Dispose();
            throw new KirokuException(500, "db_key", "データベースを開けません。鍵が合わないか、ファイルが壊れています。バックアップからの復元が必要な可能性があります。");
        }
        Exec(c, null, "PRAGMA journal_mode = WAL");
        Exec(c, null, "PRAGMA synchronous = FULL");
        Exec(c, null, "PRAGMA foreign_keys = ON");
        return c;
    }

    // ---- 小さなSQLヘルパー ----
    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? t, string sql, object?[] args)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("@" + i, args[i] ?? DBNull.Value);
        return cmd;
    }
    private static int Exec(SqliteConnection c, SqliteTransaction? t, string sql, params object?[] a) { using var m = Cmd(c, t, sql, a); return m.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection c, SqliteTransaction? t, string sql, params object?[] a) { using var m = Cmd(c, t, sql, a); var v = m.ExecuteScalar(); return v is DBNull ? null : v; }
    private static List<T> Rows<T>(SqliteConnection c, SqliteTransaction? t, string sql, object?[] a, Func<SqliteDataReader, T> map)
    {
        using var m = Cmd(c, t, sql, a); using var r = m.ExecuteReader();
        var l = new List<T>(); while (r.Read()) l.Add(map(r)); return l;
    }
    private static T? One<T>(SqliteConnection c, SqliteTransaction? t, string sql, object?[] a, Func<SqliteDataReader, T> map) where T : class
        => Rows(c, t, sql, a, map).FirstOrDefault();
    private static T? OneV<T>(SqliteConnection c, SqliteTransaction? t, string sql, object?[] a, Func<SqliteDataReader, T> map) where T : struct
    { var l = Rows(c, t, sql, a, map); return l.Count == 0 ? null : l[0]; }
    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private string Now() => Clock().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public T Read<T>(Func<SqliteConnection, T> f) { lock (_gate) return f(_db); }

    public T Tx<T>(Func<SqliteConnection, SqliteTransaction, T> f)
    {
        lock (_gate)
        {
            using var t = _db.BeginTransaction();
            try { var v = f(_db, t); t.Commit(); return v; }
            catch { t.Rollback(); throw; }
        }
    }

    private void Migrate()
    {
        lock (_gate)
        {
            Exec(_db, null, """
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS staff(
              id TEXT PRIMARY KEY, login_id TEXT NOT NULL UNIQUE COLLATE NOCASE, name TEXT NOT NULL,
              role TEXT NOT NULL CHECK(role IN('admin','staff')), can_print INTEGER NOT NULL DEFAULT 0,
              pw_hash TEXT NOT NULL, must_change INTEGER NOT NULL DEFAULT 0, active INTEGER NOT NULL DEFAULT 1,
              failed INTEGER NOT NULL DEFAULT 0, locked_until TEXT, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS devices(
              id TEXT PRIMARY KEY, name TEXT NOT NULL, token_hash TEXT, poll_hash TEXT,
              status TEXT NOT NULL CHECK(status IN('pending','approved','rejected','revoked')),
              requested_at TEXT NOT NULL, approved_at TEXT, last_seen TEXT);
            CREATE TABLE IF NOT EXISTS sessions(
              token_hash TEXT PRIMARY KEY, staff_id TEXT NOT NULL REFERENCES staff(id), device_id TEXT NOT NULL,
              created_at TEXT NOT NULL, last_seen TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS clients(
              id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_clients_key ON clients(name_key);
            CREATE TABLE IF NOT EXISTS client_history(
              seq INTEGER PRIMARY KEY AUTOINCREMENT, client_id TEXT NOT NULL, old_name TEXT NOT NULL, new_name TEXT NOT NULL,
              changed_at TEXT NOT NULL, changed_by TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS records(
              id TEXT PRIMARY KEY, client_id TEXT NOT NULL REFERENCES clients(id), date TEXT NOT NULL,
              check_in TEXT, check_out TEXT, work TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
              version INTEGER NOT NULL, created_at TEXT NOT NULL, created_by TEXT NOT NULL,
              updated_at TEXT NOT NULL, updated_by TEXT NOT NULL, updated_by_name TEXT NOT NULL,
              UNIQUE(client_id, date));
            CREATE INDEX IF NOT EXISTS ix_records_date ON records(date);
            CREATE TABLE IF NOT EXISTS record_history(
              seq INTEGER PRIMARY KEY AUTOINCREMENT, record_id TEXT NOT NULL, version INTEGER NOT NULL,
              check_in TEXT, check_out TEXT, work TEXT NOT NULL, notes TEXT NOT NULL,
              replaced_at TEXT NOT NULL, replaced_by TEXT NOT NULL, replaced_by_name TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_rh_record ON record_history(record_id);
            CREATE TABLE IF NOT EXISTS memos(
              date TEXT PRIMARY KEY, text TEXT NOT NULL, version INTEGER NOT NULL, updated_at TEXT NOT NULL,
              updated_by TEXT NOT NULL, updated_by_name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS memo_history(
              seq INTEGER PRIMARY KEY AUTOINCREMENT, date TEXT NOT NULL, version INTEGER NOT NULL, text TEXT NOT NULL,
              replaced_at TEXT NOT NULL, replaced_by TEXT NOT NULL, replaced_by_name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(
              seq INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, staff_id TEXT NOT NULL, staff_name TEXT NOT NULL,
              device_id TEXT NOT NULL, action TEXT NOT NULL, target TEXT NOT NULL, detail TEXT NOT NULL,
              prev_hash TEXT NOT NULL, hash TEXT NOT NULL);
            INSERT OR IGNORE INTO meta(key,value) VALUES('revision','0'),('schema','1');
            """);
        }
    }

    public long Revision => Read(c => long.Parse((string)Scalar(c, null, "SELECT value FROM meta WHERE key='revision'")!));
    private static void Bump(SqliteConnection c, SqliteTransaction t) => Exec(c, t, "UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='revision'");

    // ---- 監査ログ(ハッシュ連鎖) ----
    private void Audit(SqliteConnection c, SqliteTransaction t, Actor a, string action, string target = "", string detail = "")
    {
        var prev = (string?)Scalar(c, t, "SELECT hash FROM audit ORDER BY seq DESC LIMIT 1") ?? new string('0', 64);
        var at = Now();
        var hash = Hashing.Sha256Hex($"{prev}|{at}|{a.StaffId}|{a.StaffName}|{a.DeviceId}|{action}|{target}|{detail}");
        Exec(c, t, "INSERT INTO audit(at,staff_id,staff_name,device_id,action,target,detail,prev_hash,hash) VALUES(@0,@1,@2,@3,@4,@5,@6,@7,@8)",
            at, a.StaffId, a.StaffName, a.DeviceId, action, target, detail, prev, hash);
    }

    public void WriteAudit(Actor a, string action, string target = "", string detail = "") => Tx((c, t) => { Audit(c, t, a, action, target, detail); return 0; });

    public IReadOnlyList<AuditItem> ListAudit(int limit = 300, long? beforeSeq = null) => Read(c =>
        Rows(c, null, "SELECT seq,at,staff_name,device_id,action,target,detail FROM audit WHERE (@0 IS NULL OR seq<@0) ORDER BY seq DESC LIMIT @1",
            [beforeSeq, Math.Clamp(limit, 1, 2000)],
            r => new AuditItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6))));

    /// <summary>連鎖の検証。改ざん・欠落があれば最初の不整合 seq を返す。</summary>
    public (bool Ok, long? BadSeq) VerifyAudit() => Read(c =>
    {
        var prev = new string('0', 64);
        using var m = Cmd(c, null, "SELECT seq,at,staff_id,staff_name,device_id,action,target,detail,prev_hash,hash FROM audit ORDER BY seq", []);
        using var r = m.ExecuteReader();
        while (r.Read())
        {
            var expect = Hashing.Sha256Hex($"{prev}|{r.GetString(1)}|{r.GetString(2)}|{r.GetString(3)}|{r.GetString(4)}|{r.GetString(5)}|{r.GetString(6)}|{r.GetString(7)}");
            if (r.GetString(8) != prev || r.GetString(9) != expect) return (false, (long?)r.GetInt64(0));
            prev = r.GetString(9);
        }
        return (true, (long?)null);
    });

    // ---- 整合性・バックアップ・入替 ----
    public bool IntegrityOk() => Read(c => (string?)Scalar(c, null, "PRAGMA integrity_check") == "ok");

    /// <summary>暗号化DBの一貫したスナップショットを outPath に作る(同じ鍵で暗号化)。</summary>
    public void SnapshotTo(string outPath)
    {
        if (File.Exists(outPath)) File.Delete(outPath);
        lock (_gate)
        {
            using var dst = new SqliteConnection($"Data Source={outPath};Pooling=False");
            dst.Open();
            Exec(dst, null, $"PRAGMA key = \"x'{Convert.ToHexString(_key)}'\"");
            _db.BackupDatabase(dst);
            Exec(dst, null, "PRAGMA journal_mode = DELETE");
        }
        foreach (var ext in new[] { "-wal", "-shm" }) if (File.Exists(outPath + ext)) File.Delete(outPath + ext);
    }

    /// <summary>ファイルが指定鍵で開け、整合性検査に通るか。</summary>
    public static bool VerifyFile(string path, byte[] key)
    {
        try
        {
            using var c = Open(path, key, create: false);
            return (string?)Scalar(c, null, "PRAGMA integrity_check") == "ok";
        }
        catch (KirokuException) { return false; }
        finally { SqliteConnection.ClearAllPools(); }
    }

    public byte[] DbKey => (byte[])_key.Clone();
    public long DbSize => File.Exists(Path) ? new FileInfo(Path).Length : 0;

    /// <summary>復元: 検証済みの新DBファイルへ入れ替える。</summary>
    public void ReplaceWith(string newDbPath, byte[] newKey)
    {
        lock (_gate)
        {
            var test = Open(newDbPath, newKey, create: false); // 先に開けることを確認
            if ((string?)Scalar(test, null, "PRAGMA integrity_check") != "ok") { test.Dispose(); throw new KirokuException(400, "restore_corrupt", "復元するデータが壊れています。"); }
            test.Dispose();
            _db.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" }) if (File.Exists(Path + ext)) File.Delete(Path + ext);
            File.Copy(newDbPath, Path);
            _key = newKey;
            _db = Open(Path, _key);
            Migrate();
        }
    }

    public void Dispose() { lock (_gate) { _db.Dispose(); SqliteConnection.ClearAllPools(); } }
}
