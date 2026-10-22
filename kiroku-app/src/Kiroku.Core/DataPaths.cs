using System.Text.Json;

namespace Kiroku.Core;

public sealed class DataPaths(string root)
{
    public string Root { get; } = root;
    public string Db => Path.Combine(Root, "kiroku.db");
    public string DbKey => Path.Combine(Root, "db.key.protected");
    public string RecoveryWrap => Path.Combine(Root, "recovery.wrap");
    public string Cert => Path.Combine(Root, "server-cert.protected");
    public string Backups => Path.Combine(Root, "backups");
    public string Tmp => Path.Combine(Root, "tmp");
    public string Config => Path.Combine(Root, "server-config.json");

    public static DataPaths Default() => new(Path.Combine(
        Environment.GetEnvironmentVariable("KIROKU_DATA") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KirokuApp"), "data"));

    public void Ensure()
    {
        Directory.CreateDirectory(Root); Directory.CreateDirectory(Backups); Directory.CreateDirectory(Tmp);
    }

    /// <summary>前回異常終了で残った一時ファイルを消す。</summary>
    public void CleanTmp()
    {
        if (!Directory.Exists(Tmp)) return;
        foreach (var f in Directory.GetFiles(Tmp)) try { File.Delete(f); } catch { /* 使用中なら次回 */ }
    }
}

public sealed class ServerConfig
{
    public string ServerName { get; set; } = Environment.MachineName;
    public string? ExternalBackupPath { get; set; }
    public int BackupHour { get; set; } = 19;
    public int KeepLocal { get; set; } = 30;
    public int KeepExternal { get; set; } = 60;

    public static ServerConfig Load(DataPaths p)
    {
        try { return File.Exists(p.Config) ? JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(p.Config)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(DataPaths p)
    {
        var tmp = p.Config + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, p.Config, true);
    }
}

/// <summary>DB鍵の保管。DB鍵はOS保護(DPAPI)で包んだ形でのみ保存する。</summary>
public sealed class KeyStore(DataPaths paths, IKeyProtector protector)
{
    public byte[] LoadOrCreateDbKey()
    {
        if (File.Exists(paths.DbKey)) return protector.Unprotect(File.ReadAllBytes(paths.DbKey));
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        StoreDbKey(key);
        return key;
    }

    public void StoreDbKey(byte[] key)
    {
        var tmp = paths.DbKey + ".tmp";
        File.WriteAllBytes(tmp, protector.Protect(key));
        File.Move(tmp, paths.DbKey, true);
    }

    public byte[]? LoadRecoveryWrap() => File.Exists(paths.RecoveryWrap) ? File.ReadAllBytes(paths.RecoveryWrap) : null;
    public void StoreRecoveryWrap(byte[] w) => File.WriteAllBytes(paths.RecoveryWrap, w);
    public IKeyProtector Protector => protector;
}
