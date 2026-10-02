using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BtChat;

public class TcpTransport
{
    public const int Port = 49753;

    TcpListener? listener;

    public IReadOnlyList<string> GetLocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToList();

    public async Task<Stream> ConnectAsync(string host, CancellationToken ct)
    {
        AppLog.Write("TCP", $"connect to {host}:{Port}");
        var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(host, Port, timeout.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error("TCP", "connect failed", ex);
            client.Dispose();
            throw;
        }
        AppLog.Write("TCP", "connected");
        return Wrap(client);
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
                AppLog.Write("TCP", $"listening on port {Port}");
            }
            catch (Exception ex)
            {
                AppLog.Error("TCP", "listen start failed", ex);
                throw;
            }
        }
        var client = await listener.AcceptTcpClientAsync(ct);
        AppLog.Write("TCP", $"accepted from {client.Client.RemoteEndPoint}");
        return Wrap(client);
    }

    static Stream Wrap(TcpClient client)
    {
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        var stream = client.GetStream();
        return new DuplexStream(stream, stream, client.Close, "tcp");
    }
}
