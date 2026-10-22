using Microsoft.Data.Sqlite;

namespace Kiroku.Core;

public sealed partial class KirokuStore
{
    private const string RecCols = "r.id,r.client_id,c.name,r.date,r.check_in,r.check_out,r.work,r.notes,r.version,r.updated_at,r.updated_by_name";
    private const string RecFrom = "FROM records r JOIN clients c ON c.id=r.client_id";

    private static RecordDetail MapRec(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4), S(r, 5), r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetString(9), r.GetString(10));

    private static RecordSummary MapSum(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4), S(r, 5), r.GetInt32(6) == 1, r.GetInt32(7) == 1, r.GetInt32(8));
    private const string SumCols = "r.id,r.client_id,c.name,r.date,r.check_in,r.check_out,(r.work<>'') ,(r.notes<>''),r.version";

    // ---- 利用者 ----
    public IReadOnlyList<ClientInfo> ListClients(string? q = null) => Read(c =>
    {
        var key = string.IsNullOrWhiteSpace(q) ? null : Names.Key(q);
        return Rows(c, null, "SELECT id,name FROM clients WHERE (@0 IS NULL OR instr(name_key,@0)>0) ORDER BY name", [key],
            r => new ClientInfo(r.GetString(0), r.GetString(1)));
    });

    public void RenameClient(Actor a, string clientId, string newName) => Tx((c, t) =>
    {
        var nm = Names.Clean(newName) ?? throw new KirokuException(400, "bad_name", "氏名を入力してください。");
        var old = (string?)Scalar(c, t, "SELECT name FROM clients WHERE id=@0", clientId) ?? throw new KirokuException(404, "no_client", "利用者が見つかりません。");
        if (old == nm) return 0;
        Exec(c, t, "UPDATE clients SET name=@1, name_key=@2 WHERE id=@0", clientId, nm, Names.Key(nm));
        Exec(c, t, "INSERT INTO client_history(client_id,old_name,new_name,changed_at,changed_by) VALUES(@0,@1,@2,@3,@4)", clientId, old, nm, Now(), a.StaffName);
        Audit(c, t, a, "client_rename", clientId, "氏名を修正"); // 氏名そのものはログに残さない(履歴表に保持)
        Bump(c, t);
        return 0;
    });

    public IReadOnlyList<string> WorkSuggestions(string? q, int limit = 12) => Read(c =>
    {
        var like = "%" + (q ?? "").Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return Rows(c, null, """
            SELECT work FROM (SELECT work, count(*) n, max(date) d FROM records WHERE work<>'' AND work LIKE @0 ESCAPE '\' GROUP BY work)
            ORDER BY n DESC, d DESC LIMIT @1
            """, [like, Math.Clamp(limit, 1, 50)], r => r.GetString(0));
    });

    // ---- 日々の記録 ----
    public IReadOnlyList<RecordSummary> ListRecordsByDate(string date) => Read(c =>
        Rows(c, null, $"SELECT {SumCols} {RecFrom} WHERE r.date=@0 ORDER BY COALESCE(r.check_in,'99:99'), c.name", [TimeText.Date(date)], MapSum));

    public RecordDetail? GetRecord(string id) => Read(c => One(c, null, $"SELECT {RecCols} {RecFrom} WHERE r.id=@0", [id], MapRec));

    public RecordDetail? GetRecord(string clientId, string date) => Read(c =>
        One(c, null, $"SELECT {RecCols} {RecFrom} WHERE r.client_id=@0 AND r.date=@1", [clientId, TimeText.Date(date)], MapRec));

    public IReadOnlyList<RecordSummary> SearchRecords(string? name, string? from, string? to, int limit = 500) => Read(c =>
    {
        var key = string.IsNullOrWhiteSpace(name) ? null : Names.Key(name);
        return Rows(c, null, $"""
            SELECT {SumCols} {RecFrom}
            WHERE (@0 IS NULL OR instr(c.name_key,@0)>0) AND (@1 IS NULL OR r.date>=@1) AND (@2 IS NULL OR r.date<=@2)
            ORDER BY r.date DESC, c.name LIMIT @3
            """, [key, string.IsNullOrWhiteSpace(from) ? null : TimeText.Date(from), string.IsNullOrWhiteSpace(to) ? null : TimeText.Date(to), Math.Clamp(limit, 1, 2000)], MapSum);
    });

    public RecordDetail SaveRecord(Actor a, RecordSaveRequest r) => Tx((c, t) =>
    {
        var date = TimeText.Date(r.Date)!;
        var cin = TimeText.Normalize(r.CheckIn, "通所時間");
        var cout = TimeText.Normalize(r.CheckOut, "退所時間");
        if (cin != null && cout != null && string.CompareOrdinal(cout, cin) <= 0 && !r.ConfirmTimeWarning)
            throw new KirokuException(422, "time_warning", $"退所時間({cout})が通所時間({cin})より前、または同じ時刻です。このまま保存しますか?");
        var work = (r.Work ?? "").Replace("\r\n", "\n").TrimEnd();
        var notes = (r.Notes ?? "").Replace("\r\n", "\n").TrimEnd();
        if (work.Length > 5000 || notes.Length > 5000)
            throw new KirokuException(400, "too_long", "文字数が多すぎます(各5000文字まで)。");

        // 利用者の解決(氏名の自動登録)
        string clientId; var created = false;
        if (!string.IsNullOrWhiteSpace(r.ClientId))
        {
            clientId = r.ClientId;
            if (Scalar(c, t, "SELECT 1 FROM clients WHERE id=@0", clientId) == null) throw new KirokuException(404, "no_client", "利用者が見つかりません。");
        }
        else
        {
            var name = Names.Clean(r.NewClientName) ?? throw new KirokuException(400, "name_required", "氏名を入力してください。");
            if (name.Length > 60) throw new KirokuException(400, "bad_name", "氏名が長すぎます。");
            var same = Rows(c, t, "SELECT id,name FROM clients WHERE name_key=@0", [Names.Key(name)], x => new ClientInfo(x.GetString(0), x.GetString(1)));
            if (same.Count > 0 && !r.ConfirmDuplicateName)
                throw new KirokuException(409, "duplicate_name", $"「{name}」さんはすでに利用者一覧にあります。同じ方なら一覧から選んでください。別人(同姓同名)として新しく登録しますか?", same);
            clientId = Rand.Id(); created = true;
            Exec(c, t, "INSERT INTO clients(id,name,name_key,created_at) VALUES(@0,@1,@2,@3)", clientId, name, Names.Key(name), Now());
            Audit(c, t, a, "client_create", clientId);
        }

        var cur = One(c, t, $"SELECT {RecCols} {RecFrom} WHERE r.client_id=@0 AND r.date=@1", [clientId, date], MapRec);
        string id;
        if (cur == null)
        {
            if (r.ExpectedVersion != 0) throw new KirokuException(409, "conflict", "この記録は他の端末で変更されました。画面を読み込み直してください。");
            id = Rand.Id();
            Exec(c, t, "INSERT INTO records(id,client_id,date,check_in,check_out,work,notes,version,created_at,created_by,updated_at,updated_by,updated_by_name) VALUES(@0,@1,@2,@3,@4,@5,@6,1,@7,@8,@7,@8,@9)",
                id, clientId, date, cin, cout, work, notes, Now(), a.StaffId, a.StaffName);
            Audit(c, t, a, "record_create", id, date);
        }
        else
        {
            id = cur.Id;
            if (cur.CheckIn == cin && cur.CheckOut == cout && cur.Work == work && cur.Notes == notes) return cur; // 変更なし
            if (r.ExpectedVersion != cur.Version)
                throw new KirokuException(409, "conflict", "この記録は他の職員が先に保存しました。相手の内容を消さないよう、保存を止めています。", cur);
            Exec(c, t, "INSERT INTO record_history(record_id,version,check_in,check_out,work,notes,replaced_at,replaced_by,replaced_by_name) VALUES(@0,@1,@2,@3,@4,@5,@6,@7,@8)",
                id, cur.Version, cur.CheckIn, cur.CheckOut, cur.Work, cur.Notes, Now(), a.StaffId, a.StaffName);
            Exec(c, t, "UPDATE records SET check_in=@1,check_out=@2,work=@3,notes=@4,version=version+1,updated_at=@5,updated_by=@6,updated_by_name=@7 WHERE id=@0",
                id, cin, cout, work, notes, Now(), a.StaffId, a.StaffName);
            var fields = string.Join("・", new[] { cur.CheckIn != cin ? "通所" : null, cur.CheckOut != cout ? "退所" : null, cur.Work != work ? "作業" : null, cur.Notes != notes ? "様子" : null }.Where(x => x != null));
            Audit(c, t, a, "record_update", id, $"{date} 変更項目:{fields}");
        }
        Bump(c, t);
        _ = created;
        return One(c, t, $"SELECT {RecCols} {RecFrom} WHERE r.id=@0", [id], MapRec)!;
    });

    public IReadOnlyList<RecordHistoryItem> RecordHistory(Actor a, string recordId) => Tx((c, t) =>
    {
        Audit(c, t, a, "record_history_view", recordId);
        return Rows(c, t, "SELECT version,check_in,check_out,work,notes,replaced_at,replaced_by_name FROM record_history WHERE record_id=@0 ORDER BY version DESC", [recordId],
            r => new RecordHistoryItem(r.GetInt32(0), S(r, 1), S(r, 2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
    });

    // ---- 終礼メモ ----
    public MemoDetail GetMemo(string date) => Read(c =>
        One(c, null, "SELECT date,text,version,updated_at,updated_by_name FROM memos WHERE date=@0", [TimeText.Date(date)],
            r => new MemoDetail(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4)))
        ?? new MemoDetail(TimeText.Date(date)!, "", 0, null, null));

    public MemoDetail SaveMemo(Actor a, MemoSaveRequest m) => Tx((c, t) =>
    {
        var date = TimeText.Date(m.Date)!;
        var text = (m.Text ?? "").Replace("\r\n", "\n").TrimEnd();
        if (text.Length > 20000) throw new KirokuException(400, "too_long", "終礼メモが長すぎます(20000文字まで)。");
        var cur = One(c, t, "SELECT date,text,version,updated_at,updated_by_name FROM memos WHERE date=@0", [date],
            r => new MemoDetail(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4)));
        if (cur == null)
        {
            if (m.ExpectedVersion != 0) throw new KirokuException(409, "conflict", "終礼メモが他の端末で変更されました。読み込み直してください。");
            if (text == "") return new MemoDetail(date, "", 0, null, null);
            Exec(c, t, "INSERT INTO memos(date,text,version,updated_at,updated_by,updated_by_name) VALUES(@0,@1,1,@2,@3,@4)", date, text, Now(), a.StaffId, a.StaffName);
            Audit(c, t, a, "memo_create", date);
        }
        else
        {
            if (cur.Text == text) return cur;
            if (m.ExpectedVersion != cur.Version)
                throw new KirokuException(409, "conflict", "終礼メモを他の職員が先に保存しました。相手の内容を消さないよう、保存を止めています。", cur);
            Exec(c, t, "INSERT INTO memo_history(date,version,text,replaced_at,replaced_by,replaced_by_name) VALUES(@0,@1,@2,@3,@4,@5)", date, cur.Version, cur.Text, Now(), a.StaffId, a.StaffName);
            Exec(c, t, "UPDATE memos SET text=@1,version=version+1,updated_at=@2,updated_by=@3,updated_by_name=@4 WHERE date=@0", date, text, Now(), a.StaffId, a.StaffName);
            Audit(c, t, a, "memo_update", date);
        }
        Bump(c, t);
        return One(c, t, "SELECT date,text,version,updated_at,updated_by_name FROM memos WHERE date=@0", [date],
            r => new MemoDetail(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4)))!;
    });

    public IReadOnlyList<MemoHistoryItem> MemoHistory(Actor a, string date) => Tx((c, t) =>
    {
        Audit(c, t, a, "memo_history_view", date);
        return Rows(c, t, "SELECT version,text,replaced_at,replaced_by_name FROM memo_history WHERE date=@0 ORDER BY version DESC", [TimeText.Date(date)],
            r => new MemoHistoryItem(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)));
    });

    // ---- 状態 ----
    public (int ActiveStaff, int ApprovedDevices) Counts() => Read(c =>
        ((int)(long)Scalar(c, null, "SELECT count(*) FROM staff WHERE active=1")!, (int)(long)Scalar(c, null, "SELECT count(*) FROM devices WHERE status='approved'")!));
}
