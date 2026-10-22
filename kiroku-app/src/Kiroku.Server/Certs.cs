using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kiroku.Core;

namespace Kiroku.Server;

public static class Certs
{
    /// <summary>自己署名証明書。秘密鍵入りPFXはDPAPIで保護して保存。端末側は指紋で固定(ピン留め)する。</summary>
    public static X509Certificate2 LoadOrCreate(DataPaths paths, IKeyProtector protector)
    {
        if (File.Exists(paths.Cert))
            return new X509Certificate2(protector.Unprotect(File.ReadAllBytes(paths.Cert)), (string?)null, X509KeyStorageFlags.Exportable);

        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest($"CN=Kiroku-{Environment.MachineName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName); san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var tmp = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pfx = tmp.Export(X509ContentType.Pfx);
        var t = paths.Cert + ".tmp";
        File.WriteAllBytes(t, protector.Protect(pfx));
        File.Move(t, paths.Cert, true);
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
    }

    public static string Fingerprint(X509Certificate2 c) => Hashing.Sha256Hex(c.RawData);
}
