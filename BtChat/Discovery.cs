using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BtChat;

public sealed record FoundDevice(string Name, string Address)
{
    public string Display => $"{Name}  ({Address})";
}

// Finds other BtChat devices on the same network with UDP broadcast.
// Packet: BTCHAT1|REQ|<id>|<name>   (who is there?)
//         BTCHAT1|RES|<id>|<name>|<tcpPort>   (I am here)
public sealed class DiscoveryService
{
    public const int Port = 49754;
    const string Magic = "BTCHAT1";

    readonly TcpTransport tcp;
    readonly string id = Guid.NewGuid().ToString("N");
    UdpClient? udp;
#if ANDROID
    Android.Net.Wifi.WifiManager.MulticastLock? multicastLock;
#endif

    // Only answer requests while the app is in Wi-Fi mode and not already connected.
    public Func<bool> CanRespond { get; set; } = () => true;
    public event Action<FoundDevice>? DeviceFound;

    public DiscoveryService(TcpTransport tcp) => this.tcp = tcp;

    public static string DeviceName()
    {
        string name;
        try { name = DeviceInfo.Current.Name; }
        catch { name = ""; }
        name = new string(name.Where(c => c != '|' && !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = "BtChat";
        return name.Length > 40 ? name[..40] : name;
    }

    // Android drops some broadcast/multicast packets in power-save unless a multicast lock is held.
    public void SetActive(bool active)
    {
#if ANDROID
        try
        {
            if (active)
            {
                if (multicastLock == null)
                {
                    var wifi = Android.App.Application.Context.GetSystemService(Android.Content.Context.WifiService) as Android.Net.Wifi.WifiManager;
                    multicastLock = wifi?.CreateMulticastLock("btchat-discovery");
                    multicastLock?.SetReferenceCounted(false);
                }
                if (multicastLock != null && !multicastLock.IsHeld)
                {
                    multicastLock.Acquire();
                    AppLog.Write("DISCOVERY", "multicast lock acquired");
                }
            }
            else if (multicastLock != null && multicastLock.IsHeld)
            {
                multicastLock.Release();
                AppLog.Write("DISCOVERY", "multicast lock released");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("DISCOVERY", "multicast lock failed", ex);
        }
#endif
    }

    public async Task RunAsync()
    {
        while (true)
        {
            try
            {
                using var client = new UdpClient(AddressFamily.InterNetwork);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.EnableBroadcast = true;
                try
                {
                    // Windows: ignore "connection reset" errors caused by ICMP replies to our broadcasts.
                    client.Client.IOControl((IOControlCode)(-1744830452), new byte[] { 0 }, null);
                }
                catch
                {
                }
                client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                udp = client;
                AppLog.Write("DISCOVERY", $"listening on udp {Port}, id={id[..6]}");
                while (true)
                {
                    try
                    {
                        var packet = await client.ReceiveAsync();
                        Handle(client, packet);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("DISCOVERY", "udp loop error, retry in 3s", ex);
                udp = null;
                await Task.Delay(3000);
            }
        }
    }

    void Handle(UdpClient client, UdpReceiveResult packet)
    {
        var parts = Encoding.UTF8.GetString(packet.Buffer).Split('|');
        if (parts.Length < 4 || parts[0] != Magic) return;
        var kind = parts[1];
        var peerId = parts[2];
        var peerName = parts[3];
        if (peerId == id) return;
        if (kind == "REQ")
        {
            if (!CanRespond())
            {
                AppLog.Write("DISCOVERY", $"request from {packet.RemoteEndPoint} ignored (not in Wi-Fi mode or busy)");
                return;
            }
            AppLog.Write("DISCOVERY", $"request from {packet.RemoteEndPoint} ('{peerName}'), replying");
            var reply = Encoding.UTF8.GetBytes($"{Magic}|RES|{id}|{DeviceName()}|{TcpTransport.Port}");
            _ = SendAsync(client, reply, packet.RemoteEndPoint, "reply");
        }
        else if (kind == "RES")
        {
            if (tcp.IsLocalAddress(packet.RemoteEndPoint.Address)) return;
            var address = packet.RemoteEndPoint.Address.ToString();
            AppLog.Write("DISCOVERY", $"response from {address} name='{peerName}'");
            DeviceFound?.Invoke(new FoundDevice(peerName, address));
        }
    }

    static async Task SendAsync(UdpClient client, byte[] data, IPEndPoint endpoint, string what)
    {
        try
        {
            await client.SendAsync(data, data.Length, endpoint);
        }
        catch (Exception ex)
        {
            AppLog.Error("DISCOVERY", $"send {what} to {endpoint} failed", ex);
        }
    }

    List<IPAddress> BuildTargets()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var a in tcp.GetLocalAddressInfos().Where(a => !a.IsMobile && !a.Address.StartsWith("169.254.")))
        {
            try
            {
                var ip = IPAddress.Parse(a.Address).GetAddressBytes();
                var mask = IPAddress.TryParse(a.Mask, out var m) && !m.Equals(IPAddress.Any)
                    ? m.GetAddressBytes()
                    : new byte[] { 255, 255, 255, 0 };
                var bytes = new byte[4];
                for (var i = 0; i < 4; i++) bytes[i] = (byte)(ip[i] | ~mask[i]);
                var broadcast = new IPAddress(bytes);
                if (!list.Contains(broadcast)) list.Add(broadcast);
            }
            catch
            {
            }
        }
        return list;
    }

    public async Task SearchAsync(TimeSpan duration)
    {
        for (var i = 0; i < 15 && udp == null; i++) await Task.Delay(100);
        var client = udp;
        if (client == null)
        {
            AppLog.Write("DISCOVERY", "search skipped, udp socket is not ready");
            return;
        }
        var request = Encoding.UTF8.GetBytes($"{Magic}|REQ|{id}|{DeviceName()}");
        var targets = BuildTargets();
        AppLog.Write("DISCOVERY", $"search start, targets=[{string.Join(", ", targets)}]");
        const int rounds = 3;
        for (var i = 0; i < rounds; i++)
        {
            foreach (var target in targets)
                await SendAsync(client, request, new IPEndPoint(target, Port), "request");
            await Task.Delay(duration / rounds);
        }
        AppLog.Write("DISCOVERY", "search finished");
    }
}
