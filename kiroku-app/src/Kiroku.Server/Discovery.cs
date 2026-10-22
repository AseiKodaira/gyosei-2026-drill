using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kiroku.Core;

namespace Kiroku.Server;

/// <summary>同一LAN内の端末からの「親機はどこ?」に答えるUDPサービス(LAN限定・個人情報なし)。</summary>
public sealed class DiscoveryService(Func<ServerConfig> cfg, string fingerprint, int httpsPort, int discoveryPort, ILogger<DiscoveryService> log) : BackgroundService
{
    public const string Probe = "KIROKU-DISCOVER?1";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        UdpClient udp;
        try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort)); }
        catch (SocketException e) { log.LogWarning("自動検出ポートを開けませんでした: {Code}", e.SocketErrorCode); return; }
        using (udp)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(ct);
                    if (Encoding.UTF8.GetString(r.Buffer) != Probe || !Lan.IsAllowed(r.RemoteEndPoint.Address)) continue;
                    var reply = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryReply(cfg().ServerName, httpsPort, fingerprint));
                    await udp.SendAsync(reply, r.RemoteEndPoint, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { /* 一時的な受信エラーは無視 */ }
            }
        }
    }
}

public static class Lan
{
    /// <summary>事業所内LANとみなすアドレス(ループバック・プライベート・リンクローカル)。</summary>
    public static bool IsAllowed(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }
}
