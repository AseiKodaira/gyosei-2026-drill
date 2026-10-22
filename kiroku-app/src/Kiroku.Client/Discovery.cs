using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kiroku.Core;

namespace Kiroku.Client;

public sealed record FoundServer(string Host, int Port, string Name, string Fingerprint);

public static class DiscoveryClient
{
    /// <summary>LAN内の親機をUDPブロードキャストで探す。</summary>
    public static async Task<List<FoundServer>> Find(TimeSpan? wait = null, int discoveryPort = Ports.Discovery, IEnumerable<IPAddress>? targets = null)
    {
        var found = new Dictionary<string, FoundServer>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var probe = Encoding.UTF8.GetBytes("KIROKU-DISCOVER?1");
        foreach (var t in targets ?? BroadcastAddresses())
        {
            try { await udp.SendAsync(probe, new IPEndPoint(t, discoveryPort)); } catch (SocketException) { }
        }
        var end = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(2.5));
        while (DateTime.UtcNow < end)
        {
            var left = end - DateTime.UtcNow;
            using var cts = new CancellationTokenSource(left);
            try
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var d = JsonSerializer.Deserialize<DiscoveryReply>(r.Buffer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (d != null) found[r.RemoteEndPoint.Address + ":" + d.Port] = new FoundServer(r.RemoteEndPoint.Address.ToString(), d.Port, d.Name, d.Fingerprint);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { }
            catch (JsonException) { }
        }
        return found.Values.ToList();
    }

    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var a = ua.Address.GetAddressBytes(); var m = ua.IPv4Mask.GetAddressBytes();
                yield return new IPAddress(a.Select((x, i) => (byte)(x | ~m[i])).ToArray());
            }
        }
    }

    /// <summary>自動検出に失敗したときの原因切り分け。Wi-Fiの端末間通信制限(AP分離)などの可能性を文章で返す。</summary>
    public static async Task<string> Diagnose(string? manualHost, int port = Ports.Https)
    {
        var sb = new StringBuilder();
        var hasNet = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        if (!hasNet) return "このパソコンがネットワーク(Wi-Fi)につながっていません。Wi-Fiに接続してください。";
        if (string.IsNullOrWhiteSpace(manualHost))
            return "親機が見つかりませんでした。次を確認してください。\n・親機の電源が入り、Wi-Fiにつながっている\n・親機に共有サービスがインストールされている(親機で「管理」画面が開ける)\n・親機と同じWi-Fi(同じ名前・同じ帯域)に接続している\n・それでも見つからない場合、親機の「管理」画面に表示されるアドレスを下の欄に入力してください。";
        bool pingOk;
        try { using var p = new Ping(); pingOk = (await p.SendPingAsync(manualHost, 1500)).Status == IPStatus.Success; } catch { pingOk = false; }
        bool tcpOk;
        try { using var t = new TcpClient(); using var cts = new CancellationTokenSource(3000); await t.ConnectAsync(manualHost, port, cts.Token); tcpOk = true; } catch { tcpOk = false; }
        if (tcpOk) return "親機の共有サービスに接続できます。接続コードを入力して登録を進めてください。";
        if (pingOk) return "親機のパソコンには届きますが、共有サービスにつながりません。親機でサービスが動いているか、ファイアウォールの設定を確認してください。";
        return "親機に届きません。Wi-Fiルーターの「端末間通信の制限(AP分離・プライバシーセパレーター・ゲストネットワーク)」が有効だと、パソコン同士がつながりません。ルーターの設定でこの機能を無効にするか、事業所の通信担当者に確認してください。";
    }
}
