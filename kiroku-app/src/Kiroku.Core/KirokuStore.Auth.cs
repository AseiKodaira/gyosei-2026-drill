using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Kiroku.Core;

public sealed partial class KirokuStore
{
    private static readonly TimeSpan SessionIdle = TimeSpan.FromHours(4);
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockSpan = TimeSpan.FromMinutes(5);
    private static readonly string DummyHash = PasswordHasher.Hash("dummy-password-for-timing");

    private static StaffInfo MapStaff(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1, r.GetInt32(5) == 1);
    private const string StaffCols = "id,login_id,name,role,can_print,active";

    public bool IsInitialized() => Read(c => (long)Scalar(c, null, "SELECT count(*) FROM staff WHERE role='admin'")! > 0);

    // ---- 初期設定 ----
    public (StaffInfo Admin, string DeviceId, string DeviceToken) Setup(string loginId, string name, string password, string deviceName) => Tx((c, t) =>
    {
        if ((long)Scalar(c, t, "SELECT count(*) FROM staff")! > 0)
            throw new KirokuException(409, "already_setup", "すでに初期設定が完了しています。");
        var admin = InsertStaff(c, t, loginId, name, password, "admin", true, mustChange: false);
        var token = Rand.Token();
        var devId = Rand.Id();
        Exec(c, t, "INSERT INTO devices(id,name,token_hash,status,requested_at,approved_at) VALUES(@0,@1,@2,'approved',@3,@3)",
            devId, deviceName, Hashing.Sha256Hex(token), Now());
        Audit(c, t, new Actor(admin.Id, admin.Name, devId), "setup", admin.Id, "初期設定完了");
        return (admin, devId, token);
    });

    // ---- 職員 ----
    private StaffInfo InsertStaff(SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction t, string loginId, string name, string password, string role, bool canPrint, bool mustChange)
    {
        loginId = (loginId ?? "").Trim();
        if (!Regex.IsMatch(loginId, @"^[A-Za-z0-9_.\-]{3,32}$"))
            throw new KirokuException(400, "bad_login_id", "ログインIDは半角英数字(._-を含む)3〜32文字で入力してください。");
        var nm = Names.Clean(name) ?? throw new KirokuException(400, "bad_name", "職員の名前を入力してください。");
        var pwErr = PasswordHasher.Validate(password);
        if (pwErr != null) throw new KirokuException(400, "weak_password", pwErr);
        if ((long)Scalar(c, t, "SELECT count(*) FROM staff WHERE login_id=@0", loginId)! > 0)
            throw new KirokuException(409, "login_exists", "そのログインIDはすでに使われています(退職者のIDも再利用できません)。");
        var id = Rand.Id();
        Exec(c, t, "INSERT INTO staff(id,login_id,name,role,can_print,pw_hash,must_change,active,created_at) VALUES(@0,@1,@2,@3,@4,@5,@6,1,@7)",
            id, loginId, nm, role, canPrint ? 1 : 0, PasswordHasher.Hash(password), mustChange ? 1 : 0, Now());
        return new StaffInfo(id, loginId, nm, role, canPrint, true);
    }

    public StaffInfo CreateStaff(Actor a, string loginId, string name, string password, bool canPrint) => Tx((c, t) =>
    {
        var s = InsertStaff(c, t, loginId, name, password, "staff", canPrint, mustChange: true);
        Audit(c, t, a, "staff_create", s.Id, $"ログインID={s.LoginId}");
        return s;
    });

    public IReadOnlyList<StaffInfo> ListStaff() => Read(c => Rows(c, null, $"SELECT {StaffCols} FROM staff ORDER BY active DESC, name", [], MapStaff));

    public StaffInfo? GetStaff(string id) => Read(c => One(c, null, $"SELECT {StaffCols} FROM staff WHERE id=@0", [id], MapStaff));

    public void ResetPassword(Actor a, string staffId, string newPassword) => Tx((c, t) =>
    {
        var err = PasswordHasher.Validate(newPassword);
        if (err != null) throw new KirokuException(400, "weak_password", err);
        if (Exec(c, t, "UPDATE staff SET pw_hash=@1, must_change=1, failed=0, locked_until=NULL WHERE id=@0", staffId, PasswordHasher.Hash(newPassword)) == 0)
            throw new KirokuException(404, "no_staff", "職員が見つかりません。");
        Exec(c, t, "DELETE FROM sessions WHERE staff_id=@0", staffId);
        Audit(c, t, a, "staff_password_reset", staffId);
        return 0;
    });

    /// <summary>復旧用(サーバーのコマンドラインからのみ)。ログインIDでパスワードを再設定し、全セッションを失効させる。</summary>
    public void ResetPasswordByLoginId(string loginId, string newPassword) => Tx((c, t) =>
    {
        var id = (string?)Scalar(c, t, "SELECT id FROM staff WHERE login_id=@0", (loginId ?? "").Trim())
                 ?? throw new KirokuException(404, "no_staff", "そのログインIDの職員が見つかりません。");
        var err = PasswordHasher.Validate(newPassword);
        if (err != null) throw new KirokuException(400, "weak_password", err);
        Exec(c, t, "UPDATE staff SET pw_hash=@1, must_change=1, failed=0, locked_until=NULL, active=1 WHERE id=@0", id, PasswordHasher.Hash(newPassword));
        Exec(c, t, "DELETE FROM sessions");
        Audit(c, t, Actor.System, "admin_reset_cli", id, "コマンドラインから復旧キーで再設定");
        return 0;
    });

    public void ChangeOwnPassword(Actor a, string oldPassword, string newPassword) => Tx((c, t) =>
    {
        var hash = (string?)Scalar(c, t, "SELECT pw_hash FROM staff WHERE id=@0", a.StaffId);
        if (hash == null || !PasswordHasher.Verify(oldPassword, hash))
            throw new KirokuException(400, "bad_password", "現在のパスワードが違います。");
        var err = PasswordHasher.Validate(newPassword);
        if (err != null) throw new KirokuException(400, "weak_password", err);
        Exec(c, t, "UPDATE staff SET pw_hash=@1, must_change=0 WHERE id=@0", a.StaffId, PasswordHasher.Hash(newPassword));
        Audit(c, t, a, "password_change", a.StaffId);
        return 0;
    });

    public void SetStaffActive(Actor a, string staffId, bool active) => Tx((c, t) =>
    {
        var s = One(c, t, $"SELECT {StaffCols} FROM staff WHERE id=@0", [staffId], MapStaff) ?? throw new KirokuException(404, "no_staff", "職員が見つかりません。");
        if (!active && s.IsAdmin && (long)Scalar(c, t, "SELECT count(*) FROM staff WHERE role='admin' AND active=1 AND id<>@0", staffId)! == 0)
            throw new KirokuException(409, "last_admin", "最後の管理者は無効にできません。");
        Exec(c, t, "UPDATE staff SET active=@1 WHERE id=@0", staffId, active ? 1 : 0);
        if (!active) Exec(c, t, "DELETE FROM sessions WHERE staff_id=@0", staffId); // 即時ログアウト
        Audit(c, t, a, active ? "staff_enable" : "staff_disable", staffId);
        return 0;
    });

    public void SetStaffCanPrint(Actor a, string staffId, bool canPrint) => Tx((c, t) =>
    {
        if (Exec(c, t, "UPDATE staff SET can_print=@1 WHERE id=@0", staffId, canPrint ? 1 : 0) == 0)
            throw new KirokuException(404, "no_staff", "職員が見つかりません。");
        Audit(c, t, a, "staff_print_permission", staffId, canPrint ? "許可" : "取消");
        return 0;
    });

    // ---- ログイン ----
    public (LoginResult Result, bool MustChange) Login(string deviceId, string loginId, string password)
    {
        // 失敗の記録(失敗回数・監査)は必ずコミットしてから例外を投げる。
        var (ok, must, err) = Tx((c, t) => LoginTx(c, t, deviceId, loginId, password));
        if (err != null) throw err;
        return (ok!, must);
    }

    private (LoginResult? Ok, bool Must, KirokuException? Err) LoginTx(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction t, string deviceId, string loginId, string password)
    {
        var bad = new KirokuException(401, "bad_login", "ログインIDまたはパスワードが違います。");
        var rowN = OneV(c, t, "SELECT id,pw_hash,active,failed,locked_until,must_change FROM staff WHERE login_id=@0",
            [(loginId ?? "").Trim()], r => (Id: r.GetString(0), Hash: r.GetString(1), Active: r.GetInt32(2) == 1, Failed: r.GetInt32(3), Locked: S(r, 4), Must: r.GetInt32(5) == 1));
        if (rowN is not { } row)
        {
            PasswordHasher.Verify(password ?? "", DummyHash); // 時間差でIDの有無を悟らせない
            Audit(c, t, new Actor("-", "(不明なID)", deviceId), "login_failed", "", "不明なID");
            return (null, false, bad);
        }
        var staff = GetStaffTx(c, t, row.Id)!;
        var actor = new Actor(staff.Id, staff.Name, deviceId);
        if (row.Locked != null && DateTimeOffset.Parse(row.Locked) > Clock())
        {
            Audit(c, t, actor, "login_blocked", staff.Id, "ロック中");
            return (null, false, new KirokuException(423, "locked", "パスワードを続けて間違えたため、5分間ログインできません。しばらく待つか、管理者に連絡してください。"));
        }
        if (!row.Active) { Audit(c, t, actor, "login_failed", staff.Id, "無効アカウント"); return (null, false, bad); }
        if (!PasswordHasher.Verify(password ?? "", row.Hash))
        {
            var f = row.Failed + 1; var lockNow = f >= MaxFailures;
            Exec(c, t, "UPDATE staff SET failed=@1, locked_until=@2 WHERE id=@0", row.Id, lockNow ? 0 : f, lockNow ? (Clock() + LockSpan).ToString("o") : null);
            Audit(c, t, actor, "login_failed", staff.Id, lockNow ? "連続失敗によりロック" : "");
            return (null, false, bad);
        }
        var token = Rand.Token();
        Exec(c, t, "UPDATE staff SET failed=0, locked_until=NULL WHERE id=@0", row.Id);
        Exec(c, t, "INSERT INTO sessions(token_hash,staff_id,device_id,created_at,last_seen) VALUES(@0,@1,@2,@3,@3)",
            Hashing.Sha256Hex(token), row.Id, deviceId, Now());
        Audit(c, t, actor, "login", staff.Id);
        return (new LoginResult(token, staff), row.Must, null);
    }

    private static StaffInfo? GetStaffTx(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction t, string id) =>
        One(c, t, $"SELECT {StaffCols} FROM staff WHERE id=@0", [id], MapStaff);

    public void Logout(Actor a, string sessionToken) => Tx((c, t) =>
    {
        Exec(c, t, "DELETE FROM sessions WHERE token_hash=@0", Hashing.Sha256Hex(sessionToken));
        Audit(c, t, a, "logout", a.StaffId);
        return 0;
    });

    /// <summary>セッション検証。無効/期限切れ/無効化職員/端末不一致は null。</summary>
    public (StaffInfo Staff, bool MustChange)? ValidateSession(string sessionToken, string deviceId) => Tx<(StaffInfo Staff, bool MustChange)?>((c, t) =>
    {
        var h = Hashing.Sha256Hex(sessionToken);
        var sN = OneV(c, t, "SELECT staff_id,device_id,last_seen FROM sessions WHERE token_hash=@0", [h], r => (Staff: r.GetString(0), Dev: r.GetString(1), Seen: r.GetString(2)));
        if (sN is not { } s || s.Dev != deviceId) return null;
        if (DateTimeOffset.Parse(s.Seen) + SessionIdle < Clock()) { Exec(c, t, "DELETE FROM sessions WHERE token_hash=@0", h); return null; }
        var staff = GetStaffTx(c, t, s.Staff);
        if (staff == null || !staff.Active) return null;
        if (DateTimeOffset.Parse(s.Seen) + TimeSpan.FromMinutes(5) < Clock()) Exec(c, t, "UPDATE sessions SET last_seen=@1 WHERE token_hash=@0", h, Now());
        var must = (long)Scalar(c, t, "SELECT must_change FROM staff WHERE id=@0", staff.Id)! == 1;
        return (staff, must);
    });

    // ---- 端末 ----
    private string? _pairCode; private DateTimeOffset _pairExpires; private int _pairFails;
    private readonly Dictionary<string, string> _issued = [];
    private static DeviceInfo MapDev(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4));

    public (string Code, DateTimeOffset Expires) CreatePairingCode(Actor a)
    {
        lock (_gate) { _pairCode = Rand.Digits(8); _pairExpires = Clock().AddMinutes(10); _pairFails = 0; }
        WriteAudit(a, "pairing_code_issue");
        return (_pairCode!, _pairExpires);
    }

    public PairingStartResult StartPairing(string deviceName, string nonce, string proof, string serverFingerprint) => Tx((c, t) =>
    {
        if (_pairCode == null || _pairExpires < Clock())
            throw new KirokuException(400, "no_code", "接続コードの有効期限が切れています。親機の管理メニューで新しいコードを表示してください。");
        var expect = Hashing.HmacHex(_pairCode, $"pair|{nonce}|{serverFingerprint}");
        if (!Hashing.FixedEquals(expect, proof ?? ""))
        {
            if (++_pairFails >= 5) _pairCode = null;
            throw new KirokuException(400, "bad_code", "接続コードが違うか、通信先が親機ではありません。コードを確認してください。");
        }
        var name = Names.Clean(deviceName) ?? "名称未設定の端末";
        if ((long)Scalar(c, t, "SELECT count(*) FROM devices WHERE status='pending'")! >= 10)
            throw new KirokuException(429, "too_many", "承認待ちの端末が多すぎます。管理者が整理してください。");
        var id = Rand.Id(); var secret = Rand.Token(16);
        Exec(c, t, "INSERT INTO devices(id,name,poll_hash,status,requested_at) VALUES(@0,@1,@2,'pending',@3)", id, name, Hashing.Sha256Hex(secret), Now());
        var code = _pairCode; _pairCode = null; // コードは1回限り
        Audit(c, t, Actor.System, "device_request", id, name);
        return new PairingStartResult(id, secret, Hashing.HmacHex(code, $"server|{nonce}|{serverFingerprint}"));
    });

    public IReadOnlyList<DeviceInfo> ListDevices() => Read(c =>
        Rows(c, null, "SELECT id,name,status,requested_at,last_seen FROM devices ORDER BY requested_at DESC", [], MapDev));

    public void ApproveDevice(Actor a, string id) => Tx((c, t) =>
    {
        var token = Rand.Token();
        if (Exec(c, t, "UPDATE devices SET status='approved', token_hash=@1, approved_at=@2 WHERE id=@0 AND status='pending'", id, Hashing.Sha256Hex(token), Now()) == 0)
            throw new KirokuException(404, "no_device", "承認待ちの端末が見つかりません。");
        _issued[id] = token;
        Audit(c, t, a, "device_approve", id);
        return 0;
    });

    public void RejectDevice(Actor a, string id) => Tx((c, t) =>
    {
        Exec(c, t, "UPDATE devices SET status='rejected' WHERE id=@0 AND status='pending'", id);
        Audit(c, t, a, "device_reject", id);
        return 0;
    });

    public void RevokeDevice(Actor a, string id) => Tx((c, t) =>
    {
        if (Exec(c, t, "UPDATE devices SET status='revoked', token_hash=NULL WHERE id=@0 AND status='approved'", id) == 0)
            throw new KirokuException(404, "no_device", "端末が見つかりません。");
        Exec(c, t, "DELETE FROM sessions WHERE device_id=@0", id);
        _issued.Remove(id);
        Audit(c, t, a, "device_revoke", id);
        return 0;
    });

    public PairingStatus PollPairing(string requestId, string pollSecret) => Tx((c, t) =>
    {
        var dN = OneV(c, t, "SELECT status,poll_hash FROM devices WHERE id=@0", [requestId], r => (Status: r.GetString(0), Poll: S(r, 1)));
        if (dN is not { } d || d.Poll == null || !Hashing.FixedEquals(d.Poll, Hashing.Sha256Hex(pollSecret ?? "")))
            throw new KirokuException(404, "no_request", "接続要求が見つかりません。最初からやり直してください。");
        if (d.Status == "approved")
        {
            if (_issued.Remove(requestId, out var token)) return new PairingStatus("approved", token);
            return new PairingStatus("lost", null); // 親機が再起動した等。再申請が必要
        }
        return new PairingStatus(d.Status, null);
    });

    /// <summary>端末トークン検証。承認済みでなければ null。</summary>
    public string? ValidateDevice(string token) => Tx((c, t) =>
    {
        var id = (string?)Scalar(c, t, "SELECT id FROM devices WHERE status='approved' AND token_hash=@0", Hashing.Sha256Hex(token ?? ""));
        if (id != null) Exec(c, t, "UPDATE devices SET last_seen=@1 WHERE id=@0 AND (last_seen IS NULL OR last_seen < @2)", id, Now(), Clock().AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:sszzz"));
        return id;
    });
}
