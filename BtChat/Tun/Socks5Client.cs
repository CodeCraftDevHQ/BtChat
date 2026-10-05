using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BtChat.Tun;

public sealed class SocksException(int code) : IOException("socks reply " + code)
{
    public int Code { get; } = code;
}

public sealed class SocksConnection : IDisposable
{
    public Socket Socket { get; }
    public NetworkStream Stream { get; }

    public SocksConnection(Socket socket)
    {
        Socket = socket;
        Stream = new NetworkStream(socket, false);
    }

    public void Dispose()
    {
        try { Stream.Dispose(); } catch { }
        try { Socket.Dispose(); } catch { }
    }
}

public static class Socks5
{
    public static async Task<SocksConnection> ConnectAsync(string proxyHost, int proxyPort, string? user, string? pass, IPAddress target, int targetPort, CancellationToken ct, int timeoutMs = 15000)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var token = cts.Token;

            if (!IPAddress.TryParse(proxyHost, out var proxyIp))
            {
                var all = await Dns.GetHostAddressesAsync(proxyHost, AddressFamily.InterNetwork, token);
                if (all.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                proxyIp = all[0];
            }
            await socket.ConnectAsync(new IPEndPoint(proxyIp, proxyPort), token);
            var conn = new SocksConnection(socket);
            try
            {
                await HandshakeAsync(conn.Stream, user, pass, target, targetPort, token);
            }
            catch
            {
                conn.Dispose();
                throw;
            }
            return conn;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    static async Task HandshakeAsync(Stream s, string? user, string? pass, IPAddress target, int port, CancellationToken ct)
    {
        var useAuth = !string.IsNullOrEmpty(user);
        await s.WriteAsync(useAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct);
        var b = new byte[2];
        await ReadExactAsync(s, b, 2, ct);
        if (b[0] != 5) throw new IOException("not a socks5 server");
        if (b[1] == 2)
        {
            if (!useAuth) throw new SocksException(0xFF);
            var u = Encoding.UTF8.GetBytes(user!);
            var p = Encoding.UTF8.GetBytes(pass ?? "");
            if (u.Length > 255 || p.Length > 255) throw new SocksException(0xFF);
            var request = new byte[3 + u.Length + p.Length];
            request[0] = 1;
            request[1] = (byte)u.Length;
            Buffer.BlockCopy(u, 0, request, 2, u.Length);
            request[2 + u.Length] = (byte)p.Length;
            Buffer.BlockCopy(p, 0, request, 3 + u.Length, p.Length);
            await s.WriteAsync(request, ct);
            await ReadExactAsync(s, b, 2, ct);
            if (b[1] != 0) throw new SocksException(0xFF);
        }
        else if (b[1] != 0)
        {
            throw new SocksException(0xFF);
        }

        var connect = new byte[10];
        connect[0] = 5;
        connect[1] = 1;
        connect[3] = 1;
        target.TryWriteBytes(connect.AsSpan(4, 4), out _);
        connect[8] = (byte)(port >> 8);
        connect[9] = (byte)port;
        await s.WriteAsync(connect, ct);

        var head = new byte[4];
        await ReadExactAsync(s, head, 4, ct);
        if (head[0] != 5) throw new IOException("bad socks reply");
        if (head[1] != 0) throw new SocksException(head[1]);
        int skip;
        if (head[3] == 1) skip = 6;
        else if (head[3] == 4) skip = 18;
        else
        {
            var len = new byte[1];
            await ReadExactAsync(s, len, 1, ct);
            skip = len[0] + 2;
        }
        await ReadExactAsync(s, new byte[skip], skip, ct);
    }

    public static async Task ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
