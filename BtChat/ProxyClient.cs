using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace BtChat;

// Makes this device use a proxy system-wide, where the platform allows it (Windows).
public interface ISystemProxy
{
    bool CanApply { get; }
    // True while a proxy set by this app is active (it must be restored).
    bool IsApplied { get; }
    void Apply(string host, int port);
    // Puts the settings back the way they were before Apply. Safe to call when nothing was applied.
    void Restore();
    // Android cannot set the Wi-Fi proxy from an app, but can open the screen where the user does it.
    bool CanOpenNetworkSettings { get; }
    void OpenNetworkSettings();
}

#if !ANDROID && !WINDOWS
public sealed class NoSystemProxy : ISystemProxy
{
    public bool CanApply => false;
    public bool IsApplied => false;
    public void Apply(string host, int port) => throw new NotSupportedException();
    public void Restore() { }
    public bool CanOpenNetworkSettings => false;
    public void OpenNetworkSettings() { }
}
#endif

public enum ProbeStatus
{
    Ok,
    Unreachable,     // could not open a connection to the proxy
    Timeout,         // the proxy did not answer
    Rejected,        // the proxy closed the connection right away
    AuthFailed,      // user name / password missing or wrong
    TargetRefused,   // the proxy works but the internet is not reachable through it
    NotAProxy,       // something answered, but it does not speak SOCKS5
    Error
}

public sealed record ProbeResult(ProbeStatus Status, int LatencyMs = 0, string Detail = "");

// Opens a SOCKS5 session to the proxy and fetches a tiny page through it. This checks the address,
// the password and that the proxy device really reaches the internet (VPN included).
public static class ProxyProbe
{
    static readonly (string Host, string Path)[] Targets =
    {
        ("connectivitycheck.gstatic.com", "/generate_204"),
        ("www.msftconnecttest.com", "/connecttest.txt"),
    };

    static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    public static async Task<ProbeResult> RunAsync(string host, int port, string? user, string? pass, CancellationToken ct)
    {
        ProbeResult last = new(ProbeStatus.Error);
        foreach (var (target, path) in Targets)
        {
            var result = await TryTargetAsync(host, port, user, pass, target, path, ct);
            if (result.Status == ProbeStatus.Ok) return result;
            // a problem with the proxy itself will not go away with another test page
            if (result.Status is ProbeStatus.Unreachable or ProbeStatus.Timeout or ProbeStatus.Rejected
                or ProbeStatus.AuthFailed or ProbeStatus.NotAProxy or ProbeStatus.Error)
                return result;
            last = result;
        }
        return last;
    }

    static async Task<ProbeResult> TryTargetAsync(string host, int port, string? user, string? pass, string target, string path, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var stage = 0;   // 0 = opening the connection, 1 = SOCKS handshake, 2 = talking to the test page
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StepTimeout);
            var token = cts.Token;

            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, token);
            stage = 1;
            var s = client.GetStream();

            var useAuth = !string.IsNullOrEmpty(user);
            await s.WriteAsync(useAuth ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 }, token);
            var b = new byte[2];
            await ReadExactAsync(s, b, 2, token);
            if (b[0] != 0x05) return new ProbeResult(ProbeStatus.NotAProxy);
            if (b[1] == 0xFF) return new ProbeResult(ProbeStatus.AuthFailed, 0, useAuth ? "rejected" : "required");
            if (b[1] == 0x02)
            {
                if (!useAuth) return new ProbeResult(ProbeStatus.AuthFailed, 0, "required");
                var u = Encoding.UTF8.GetBytes(user!);
                var p = Encoding.UTF8.GetBytes(pass ?? "");
                if (u.Length > 255 || p.Length > 255) return new ProbeResult(ProbeStatus.AuthFailed, 0, "too long");
                var request = new byte[3 + u.Length + p.Length];
                request[0] = 0x01;
                request[1] = (byte)u.Length;
                Buffer.BlockCopy(u, 0, request, 2, u.Length);
                request[2 + u.Length] = (byte)p.Length;
                Buffer.BlockCopy(p, 0, request, 3 + u.Length, p.Length);
                await s.WriteAsync(request, token);
                await ReadExactAsync(s, b, 2, token);
                if (b[1] != 0x00) return new ProbeResult(ProbeStatus.AuthFailed, 0, "rejected");
            }
            else if (b[1] != 0x00)
            {
                return new ProbeResult(ProbeStatus.NotAProxy);
            }

            stage = 2;
            var name = Encoding.ASCII.GetBytes(target);
            var connect = new byte[7 + name.Length];
            connect[0] = 0x05;
            connect[1] = 0x01;
            connect[2] = 0x00;
            connect[3] = 0x03;
            connect[4] = (byte)name.Length;
            Buffer.BlockCopy(name, 0, connect, 5, name.Length);
            connect[5 + name.Length] = 0x00;
            connect[6 + name.Length] = 80;
            await s.WriteAsync(connect, token);

            var head = new byte[4];
            await ReadExactAsync(s, head, 4, token);
            if (head[0] != 0x05) return new ProbeResult(ProbeStatus.NotAProxy);
            if (head[1] != 0x00) return new ProbeResult(ProbeStatus.TargetRefused, 0, "code " + head[1]);
            int skip;
            if (head[3] == 0x01) skip = 6;
            else if (head[3] == 0x04) skip = 18;
            else if (head[3] == 0x03)
            {
                var len = new byte[1];
                await ReadExactAsync(s, len, 1, token);
                skip = len[0] + 2;
            }
            else return new ProbeResult(ProbeStatus.NotAProxy);
            await ReadExactAsync(s, new byte[skip], skip, token);

            var get = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {target}\r\nUser-Agent: BtChat\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(get, token);
            var buf = new byte[16];
            var got = 0;
            while (got < 12)
            {
                var n = await s.ReadAsync(buf.AsMemory(got), token);
                if (n == 0) break;
                got += n;
            }
            var text = Encoding.ASCII.GetString(buf, 0, got);
            if (text.StartsWith("HTTP/", StringComparison.Ordinal) && text.Length >= 12 &&
                int.TryParse(text.AsSpan(9, 3), out var code))
            {
                if (code >= 200 && code < 400)
                    return new ProbeResult(ProbeStatus.Ok, (int)watch.ElapsedMilliseconds, target);
                return new ProbeResult(ProbeStatus.TargetRefused, 0, "HTTP " + code);
            }
            return new ProbeResult(ProbeStatus.TargetRefused, 0, "no reply");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return stage >= 2 ? new ProbeResult(ProbeStatus.TargetRefused, 0, "timeout") : new ProbeResult(ProbeStatus.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException ex) when (stage == 0)
        {
            return new ProbeResult(ProbeStatus.Unreachable, 0, ex.SocketErrorCode.ToString());
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or SocketException)
        {
            return stage >= 2 ? new ProbeResult(ProbeStatus.TargetRefused, 0, "closed") : new ProbeResult(ProbeStatus.Rejected);
        }
        catch (Exception ex)
        {
            AppLog.Error("ProxyClient", "probe failed", ex);
            return new ProbeResult(ProbeStatus.Error, 0, ex.Message);
        }
    }

    static async Task ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
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
