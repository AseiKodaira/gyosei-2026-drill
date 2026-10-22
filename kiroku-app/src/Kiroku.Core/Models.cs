namespace Kiroku.Core;

public static class Ports
{
    public const int Https = 47321;
    public const int Discovery = 47322;
}

public sealed record StaffInfo(string Id, string LoginId, string Name, string Role, bool CanPrint, bool Active)
{
    public bool IsAdmin => Role == "admin";
}

public sealed record LoginResult(string SessionToken, StaffInfo Staff);

public sealed record ClientInfo(string Id, string Name);

/// <summary>日付別一覧の1行。</summary>
public sealed record RecordSummary(string RecordId, string ClientId, string ClientName, string Date,
    string? CheckIn, string? CheckOut, bool HasWork, bool HasNotes, int Version);

public sealed record RecordDetail(string Id, string ClientId, string ClientName, string Date,
    string? CheckIn, string? CheckOut, string Work, string Notes, int Version,
    string UpdatedAt, string UpdatedByName);

public sealed record RecordSaveRequest(string? ClientId, string? NewClientName, string Date,
    string? CheckIn, string? CheckOut, string? Work, string? Notes,
    int ExpectedVersion, bool ConfirmDuplicateName = false, bool ConfirmTimeWarning = false);

public sealed record RecordHistoryItem(int Version, string? CheckIn, string? CheckOut, string Work, string Notes,
    string ReplacedAt, string ReplacedByName);

public sealed record MemoDetail(string Date, string Text, int Version, string? UpdatedAt, string? UpdatedByName);

public sealed record MemoSaveRequest(string Date, string Text, int ExpectedVersion);

public sealed record MemoHistoryItem(int Version, string Text, string ReplacedAt, string ReplacedByName);

public sealed record DeviceInfo(string Id, string Name, string Status, string RequestedAt, string? LastSeen);

public sealed record AuditItem(long Seq, string At, string StaffName, string DeviceId, string Action, string Target, string Detail);

public sealed record BackupFile(string Name, long Size, string CreatedAt, string Location);

public sealed record BackupStatus(string? LastLocalAt, string? LastExternalAt, string? ExternalPath,
    bool ExternalReachable, long FreeBytes, long DbBytes, IReadOnlyList<string> Warnings, IReadOnlyList<BackupFile> Files);

public sealed record SystemStatus(string Version, string ServerName, bool DbOk, int StaffActive, int DevicesApproved,
    string? LastBackupAt, bool AuditChainOk, long Revision);

public sealed record HelloInfo(string ServerName, string Fingerprint, bool Initialized, string Version);

public sealed record PairingStartResult(string RequestId, string PollSecret, string ServerProof);
public sealed record PairingStatus(string Status, string? DeviceToken);

public sealed record ApiError(string Code, string Message, object? Detail = null);

/// <summary>業務ルール違反。コードとメッセージ(日本語)をクライアントへ返す。</summary>
public sealed class KirokuException(int status, string code, string message, object? detail = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public object? Detail { get; } = detail;
}

// ---- API 要求 ----
public sealed record SetupRequest(string LoginId, string Name, string Password, string DeviceName);
public sealed record SetupResult(StaffInfo Admin, string DeviceToken, string RecoveryKey, string Fingerprint);
public sealed record LoginRequest(string LoginId, string Password);
public sealed record LoginResponse(string SessionToken, StaffInfo Staff, bool MustChangePassword);
public sealed record PairStartRequest(string DeviceName, string Nonce, string Proof);
public sealed record PairingCodeResult(string Code, string ExpiresAt);
public sealed record StaffCreateRequest(string LoginId, string Name, string Password, bool CanPrint);
public sealed record PasswordSetRequest(string NewPassword);
public sealed record PasswordChangeRequest(string OldPassword, string NewPassword);
public sealed record BoolRequest(bool Value);
public sealed record RenameRequest(string Name);
public sealed record PrintAuthRequest(string Kind, string Date);
public sealed record BackupConfigRequest(string? ExternalPath);
public sealed record RestoreRequest(string Location, string FileName, string RecoveryKey);
public sealed record VerifyItem(string Name, string Location, bool Ok);
public sealed record RecoveryKeyResult(string RecoveryKey);
public sealed record PulseResult(long Revision);
public sealed record DiscoveryReply(string Name, int Port, string Fingerprint);
