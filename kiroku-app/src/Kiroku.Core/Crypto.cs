using System.Security.Cryptography;
using System.Text;

namespace Kiroku.Core;

public static class Rand
{
    public static string Token(int bytes = 32) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    public static string Id() => Guid.NewGuid().ToString("N");
    public static string Digits(int n)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < n; i++) sb.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        return sb.ToString();
    }
}

public static class Hashing
{
    public static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    public static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public static string HmacHex(string key, string msg) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(msg))).ToLowerInvariant();

    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>パスワードは PBKDF2-HMAC-SHA256 (60万回) + ソルト。形式: pbkdf2$iter$salt$hash</summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(h)}";
    }

    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2") return false;
        var iter = int.Parse(p[1]);
        var h = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[2]), iter, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(p[3]));
    }

    /// <summary>パスワード強度の最低条件(10文字以上)。</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 10)
            return "パスワードは10文字以上にしてください。";
        if (password.Distinct().Count() < 4)
            return "パスワードが単純すぎます。別の文字も混ぜてください。";
        return null;
    }
}

/// <summary>端末(Windows)に紐づく鍵保護。Windows では DPAPI(LocalMachine)。</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public sealed class DpapiProtector(bool machineScope) : IKeyProtector
{
    private readonly DataProtectionScope _scope = machineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
    public byte[] Protect(byte[] d) => ProtectedData.Protect(d, Encoding.UTF8.GetBytes("Kiroku.v1"), _scope);
    public byte[] Unprotect(byte[] d) => ProtectedData.Unprotect(d, Encoding.UTF8.GetBytes("Kiroku.v1"), _scope);
}

/// <summary>
/// 開発・試験専用。鍵を同じフォルダに平文で置くため安全ではない。
/// Windows 以外、または環境変数 KIROKU_ALLOW_INSECURE_KEYSTORE=1 のときだけ使える。
/// </summary>
public sealed class InsecureFileProtector(string dir) : IKeyProtector
{
    private byte[] Key()
    {
        var p = Path.Combine(dir, "INSECURE-DEV-KEY.bin");
        if (!File.Exists(p)) { Directory.CreateDirectory(dir); File.WriteAllBytes(p, RandomNumberGenerator.GetBytes(32)); }
        return File.ReadAllBytes(p);
    }

    public byte[] Protect(byte[] d)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[d.Length]; var tag = new byte[16];
        using var g = new AesGcm(Key(), 16);
        g.Encrypt(nonce, d, ct, tag);
        return [.. nonce, .. tag, .. ct];
    }

    public byte[] Unprotect(byte[] d)
    {
        var pt = new byte[d.Length - 28];
        using var g = new AesGcm(Key(), 16);
        g.Decrypt(d.AsSpan(0, 12), d.AsSpan(28), d.AsSpan(12, 16), pt);
        return pt;
    }
}

public static class KeyProtectors
{
    public static IKeyProtector ForServer(string dataDir)
    {
        if (OperatingSystem.IsWindows()) return new DpapiProtector(machineScope: true);
        if (Environment.GetEnvironmentVariable("KIROKU_ALLOW_INSECURE_KEYSTORE") == "1") return new InsecureFileProtector(dataDir);
        throw new InvalidOperationException("この環境では安全な鍵保管ができません(Windows専用)。");
    }
}

/// <summary>
/// 復旧キー: 紛失時でも暗号化バックアップを開けるための 160bit キー(Base32、5文字×8グループ)。
/// DBの鍵 K はこのキーから導出した鍵で包んで保存する(recovery.wrap / 各バックアップのヘッダ)。
/// </summary>
public static class RecoveryKey
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 紛らわしい 0/O/1/I を除く(32文字)

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(25); // 200bit -> 40文字
        var chars = new char[40];
        for (var i = 0; i < 40; i++)
        {
            var bit = i * 5;
            var v = (bytes[bit / 8] << 8 | (bit / 8 + 1 < bytes.Length ? bytes[bit / 8 + 1] : 0)) >> (16 - 5 - bit % 8) & 31;
            chars[i] = Alphabet[v];
        }
        var s = new string(chars);
        return string.Join("-", Enumerable.Range(0, 8).Select(i => s.Substring(i * 5, 5)));
    }

    public static string Normalize(string key) =>
        new string(key.ToUpperInvariant().Where(c => Alphabet.Contains(c)).ToArray());

    public static bool LooksValid(string key) => Normalize(key).Length == 40;

    public static byte[] Kek(string key, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Normalize(key), salt, 300_000, HashAlgorithmName.SHA256, 32);
}

/// <summary>復旧キーで包んだDB鍵(salt16|nonce12|tag16|wrapped32 = 76バイト)。復旧キー本体は保存しない。</summary>
public static class RecoveryWrap
{
    private static readonly byte[] Aad = "KRWRAP1"u8.ToArray();
    public const int Size = 16 + 12 + 16 + 32;

    public static byte[] Create(byte[] dbKey, string recoveryKey)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var wrapped = new byte[dbKey.Length]; var tag = new byte[16];
        using var g = new AesGcm(RecoveryKey.Kek(recoveryKey, salt), 16);
        g.Encrypt(nonce, dbKey, wrapped, tag, Aad);
        return [.. salt, .. nonce, .. tag, .. wrapped];
    }

    public static byte[] Unwrap(byte[] blob, string recoveryKey)
    {
        var dbKey = new byte[32];
        try
        {
            using var g = new AesGcm(RecoveryKey.Kek(recoveryKey, blob[..16]), 16);
            g.Decrypt(blob.AsSpan(16, 12), blob.AsSpan(44, 32), blob.AsSpan(28, 16), dbKey, Aad);
        }
        catch (CryptographicException)
        {
            throw new KirokuException(400, "recovery_key", "復旧キーが違います。控えている復旧キーを確認してください。");
        }
        return dbKey;
    }
}

/// <summary>バックアップファイル形式: "KRBK1" | wrap76 | nonce12 | tag16 | ciphertext</summary>
public static class BackupCrypto
{
    private static readonly byte[] Magic = "KRBK1"u8.ToArray();

    public static void Encrypt(string plainDbPath, string outPath, byte[] dbKey, byte[] wrap)
    {
        var data = File.ReadAllBytes(plainDbPath);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[data.Length]; var tag = new byte[16];
        using (var g = new AesGcm(DeriveBodyKey(dbKey), 16)) g.Encrypt(nonce, data, ct, tag, Magic);

        var tmp = outPath + ".tmp";
        using (var fs = File.Create(tmp))
        {
            fs.Write(Magic); fs.Write(wrap); fs.Write(nonce); fs.Write(tag); fs.Write(ct);
            fs.Flush(true);
        }
        File.Move(tmp, outPath, true);
    }

    /// <summary>復旧キーで復号し、SQLCipher暗号化済みDBファイルを書き出してDB鍵を返す。</summary>
    public static byte[] Decrypt(string backupPath, string outDbPath, string recoveryKey) => Decrypt(backupPath, outDbPath, recoveryKey, null);

    /// <summary>稼働中のDB鍵で復号(バックアップの自動検証用。復旧キー不要)。</summary>
    public static void DecryptWithDbKey(string backupPath, string outDbPath, byte[] dbKey) => Decrypt(backupPath, outDbPath, null, dbKey);

    public static byte[] ReadWrap(string backupPath)
    {
        using var fs = File.OpenRead(backupPath);
        var b = new byte[5 + RecoveryWrap.Size];
        if (fs.Read(b) != b.Length || !b.AsSpan(0, 5).SequenceEqual(Magic))
            throw new KirokuException(400, "backup_format", "バックアップファイルの形式が正しくありません。");
        return b[5..];
    }

    private static byte[] Decrypt(string backupPath, string outDbPath, string? recoveryKey, byte[]? knownKey)
    {
        var all = File.ReadAllBytes(backupPath);
        if (all.Length < 5 + RecoveryWrap.Size + 28 || !all.AsSpan(0, 5).SequenceEqual(Magic))
            throw new KirokuException(400, "backup_format", "バックアップファイルの形式が正しくありません。");
        var dbKey = knownKey ?? RecoveryWrap.Unwrap(all[5..(5 + RecoveryWrap.Size)], recoveryKey!);
        var o = 5 + RecoveryWrap.Size;
        var nonce = all[o..(o += 12)]; var tag = all[o..(o += 16)]; var ct = all[o..];
        var pt = new byte[ct.Length];
        try
        {
            using var g = new AesGcm(DeriveBodyKey(dbKey), 16);
            g.Decrypt(nonce, ct, tag, pt, Magic);
        }
        catch (CryptographicException)
        {
            throw new KirokuException(400, "backup_corrupt", "バックアップファイルが壊れているか、改ざんされています。");
        }
        File.WriteAllBytes(outDbPath, pt);
        return dbKey;
    }

    private static byte[] DeriveBodyKey(byte[] dbKey) => HKDF.DeriveKey(HashAlgorithmName.SHA256, dbKey, 32, null, "kiroku-backup-body"u8.ToArray());
}
