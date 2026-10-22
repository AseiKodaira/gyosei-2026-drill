using System.Text.Json;
using Kiroku.Core;

namespace Kiroku.Client;

/// <summary>端末ごとの設定(親機/子機の別、接続先、端末トークン)。トークンはDPAPI(端末単位)で保護。</summary>
public sealed class ClientSettings
{
    public string Mode { get; set; } = ""; // "parent" | "child" | ""
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = Ports.Https;
    public string Fingerprint { get; set; } = "";
    public string? ProtectedDeviceToken { get; set; }
    public string DeviceName { get; set; } = Environment.MachineName;

    [System.Text.Json.Serialization.JsonIgnore] public bool IsConfigured => !string.IsNullOrEmpty(ProtectedDeviceToken) && Fingerprint != "";
}

public sealed class LocalState(string dir, IKeyProtector protector)
{
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KirokuApp", "device");
    private string File_ => Path.Combine(dir, "client.json");

    public ClientSettings Load()
    {
        try { return System.IO.File.Exists(File_) ? JsonSerializer.Deserialize<ClientSettings>(System.IO.File.ReadAllText(File_)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(ClientSettings s)
    {
        Directory.CreateDirectory(dir);
        var t = File_ + ".tmp";
        System.IO.File.WriteAllText(t, JsonSerializer.Serialize(s));
        System.IO.File.Move(t, File_, true);
    }

    public void SetToken(ClientSettings s, string token) => s.ProtectedDeviceToken = Convert.ToBase64String(protector.Protect(System.Text.Encoding.UTF8.GetBytes(token)));
    public string? GetToken(ClientSettings s) => s.ProtectedDeviceToken == null ? null : System.Text.Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(s.ProtectedDeviceToken)));

    public KirokuApi? CreateApi(ClientSettings s) => s.IsConfigured ? new KirokuApi(s.Host, s.Port, s.Fingerprint, GetToken(s)) : null;
}

/// <summary>保存できなかった入力内容の退避。ログイン中の職員のWindowsアカウントでのみ復号できる形で保存し、保存成功で削除する。</summary>
public sealed class DraftStore(string dir, IKeyProtector protector)
{
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KirokuApp", "drafts");

    public sealed record Draft(string Kind, string Key, string Json, string SavedAt);

    private string PathFor(string kind, string key) => Path.Combine(dir, Hashing.Sha256Hex(kind + "|" + key)[..24] + ".draft");

    public void Save(string kind, string key, string json)
    {
        Directory.CreateDirectory(dir);
        var d = JsonSerializer.Serialize(new Draft(kind, key, json, DateTime.Now.ToString("s")));
        System.IO.File.WriteAllBytes(PathFor(kind, key), protector.Protect(System.Text.Encoding.UTF8.GetBytes(d)));
    }

    public Draft? Load(string kind, string key)
    {
        try
        {
            var p = PathFor(kind, key);
            return System.IO.File.Exists(p) ? JsonSerializer.Deserialize<Draft>(System.Text.Encoding.UTF8.GetString(protector.Unprotect(System.IO.File.ReadAllBytes(p)))) : null;
        }
        catch { return null; }
    }

    public void Delete(string kind, string key) { try { System.IO.File.Delete(PathFor(kind, key)); } catch { } }

    public IReadOnlyList<Draft> All()
    {
        if (!Directory.Exists(dir)) return [];
        var l = new List<Draft>();
        foreach (var f in Directory.GetFiles(dir, "*.draft"))
            try { l.Add(JsonSerializer.Deserialize<Draft>(System.Text.Encoding.UTF8.GetString(protector.Unprotect(System.IO.File.ReadAllBytes(f))))!); } catch { }
        return l;
    }
}
