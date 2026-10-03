using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BtChat;

public record LocalAddress(string Interface, string Address, bool IsMobile, string? Mask);

public class TcpTransport
{
    public const int Port = 49753;

    TcpListener? listener;

    // Mobile-data interfaces: their address is useless for a local link.
    static bool IsMobileInterface(string name)
    {
        var n = name.ToLowerInvariant();
        return n.StartsWith("rmnet") || n.StartsWith("v4-rmnet") || n.StartsWith("ccmni") ||
               n.StartsWith("pdp") || n.StartsWith("ppp") || n.StartsWith("wwan") || n.StartsWith("clat");
    }

    // log=true writes every interface (even down / loopback / mobile) to the app log.
    public IReadOnlyList<LocalAddress> GetLocalAddressInfos(bool log = false)
    {
        var result = new List<LocalAddress>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ips = new List<(string Ip, string? Mask)>();
                try
                {
                    foreach (var a in n.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string? mask = null;
                        try { mask = a.IPv4Mask?.ToString(); } catch { }
                        ips.Add((a.Address.ToString(), mask));
                    }
                }
                catch (Exception ex)
                {
                    if (log) AppLog.Error("TCP", $"iface {n.Name} read failed", ex);
                }
                if (log)
                    AppLog.Write("TCP", $"iface {n.Name} type={n.NetworkInterfaceType} status={n.OperationalStatus} ipv4=[{string.Join(", ", ips.Select(i => i.Ip + "/" + (i.Mask ?? "?")))}]");
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var (ip, mask) in ips)
                    if (!result.Any(r => r.Address == ip))
                        result.Add(new LocalAddress(n.Name, ip, IsMobileInterface(n.Name), mask));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("TCP", "enumerate interfaces failed", ex);
        }
        return result;
    }

    // Addresses worth showing to the user (Wi-Fi, hotspot, Ethernet).
    public IReadOnlyList<string> GetLocalAddresses() =>
        GetLocalAddressInfos()
            .Where(a => !a.IsMobile && !a.Address.StartsWith("169.254."))
            .Select(a => a.Address)
            .ToList();

    public bool IsLocalAddress(IPAddress ip) =>
        IPAddress.IsLoopback(ip) || GetLocalAddressInfos().Any(a => a.Address == ip.ToString());

    // Drops our own addresses and puts the ones in the same /24 as one of our networks first.
    public IReadOnlyList<string> OrderCandidates(IEnumerable<string> ips)
    {
        var locals = GetLocalAddressInfos().Where(a => !a.IsMobile).Select(a => a.Address).ToList();
        static string Prefix(string ip)
        {
            var p = ip.Split('.');
            return p.Length == 4 ? string.Join('.', p[0], p[1], p[2]) : ip;
        }
        var prefixes = locals.Select(Prefix).ToHashSet();
        return ips
            .Where(ip => !locals.Contains(ip) && !ip.StartsWith("127."))
            .OrderByDescending(ip => prefixes.Contains(Prefix(ip)))
            .ToList();
    }

    public void LogNetworkState(string reason)
    {
        AppLog.Write("TCP", $"network state ({reason}), listener={(listener == null ? "not started" : "port " + Port)}");
        var all = GetLocalAddressInfos(log: true);
        var shown = GetLocalAddresses();
        AppLog.Write("TCP", shown.Count > 0
            ? $"addresses shown to user: {string.Join(", ", shown)}"
            : $"NO usable local address (all up addresses: {string.Join(", ", all.Select(a => a.Interface + "=" + a.Address))})");
    }

    public async Task<Stream> ConnectAsync(string host, CancellationToken ct, int timeoutSeconds = 8)
    {
        AppLog.Write("TCP", $"connect to {host}:{Port} (timeout {timeoutSeconds}s)");
        var sw = Stopwatch.StartNew();
        var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await client.ConnectAsync(host, Port, timeout.Token);
        }
        catch (Exception ex)
        {
            var code = ex is SocketException se ? se.SocketErrorCode.ToString() : ex.GetType().Name;
            AppLog.Error("TCP", $"connect to {host} failed after {sw.ElapsedMilliseconds}ms ({code})", ex);
            client.Dispose();
            throw;
        }
        AppLog.Write("TCP", $"connected after {sw.ElapsedMilliseconds}ms");
        return Wrap(client, "out");
    }

    public async Task<Stream> AcceptAsync(CancellationToken ct)
    {
        if (listener == null)
        {
            try
            {
                var created = new TcpListener(IPAddress.Any, Port);
                created.Start();
                listener = created;
                AppLog.Write("TCP", $"listening on 0.0.0.0:{Port}");
            }
            catch (Exception ex)
            {
                AppLog.Error("TCP", "listen start failed", ex);
                throw;
            }
        }
        var client = await listener.AcceptTcpClientAsync(ct);
        AppLog.Write("TCP", $"accepted from {client.Client.RemoteEndPoint}");
        return Wrap(client, "in");
    }

    static Stream Wrap(TcpClient client, string direction)
    {
        try
        {
            AppLog.Write("TCP", $"socket {direction} local={client.Client.LocalEndPoint} remote={client.Client.RemoteEndPoint}");
        }
        catch
        {
        }
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        var stream = client.GetStream();
        return new DuplexStream(stream, stream, client.Close, "tcp-" + direction);
    }
}
