using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace BtChat;

// Pairing between the sharing device (it dials out) and the receiving device (it waits):
// the receiver sends a random challenge, the sharing device answers with a magic word plus
// HMAC-SHA256(password, context + challenge). The password itself never goes over the network.
public static class ReverseProtocol
{
    public const int ChallengeSize = 16;
    const int MacSize = 32;
    static readonly byte[] Magic = { (byte)'B', (byte)'T', (byte)'R', (byte)'V' };
    static readonly byte[] Context = Encoding.ASCII.GetBytes("BtChat-reverse-v1");

    public static int ReplySize => Magic.Length + MacSize;

    public static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(ChallengeSize);

    public static byte[] BuildReply(string secret, byte[] challenge)
    {
        var reply = new byte[ReplySize];
        Buffer.BlockCopy(Magic, 0, reply, 0, Magic.Length);
        var mac = Mac(secret, challenge);
        Buffer.BlockCopy(mac, 0, reply, Magic.Length, mac.Length);
        return reply;
    }

    public static bool Verify(string secret, byte[] challenge, byte[] reply)
    {
        if (reply.Length != ReplySize) return false;
        for (var i = 0; i < Magic.Length; i++)
            if (reply[i] != Magic[i]) return false;
        var expected = Mac(secret, challenge);
        return CryptographicOperations.FixedTimeEquals(expected, reply.AsSpan(Magic.Length, MacSize));
    }

    static byte[] Mac(string secret, byte[] challenge)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? ""));
        var data = new byte[Context.Length + challenge.Length];
        Buffer.BlockCopy(Context, 0, data, 0, Context.Length);
        Buffer.BlockCopy(challenge, 0, data, Context.Length, challenge.Length);
        return hmac.ComputeHash(data);
    }

    public static async Task ReceiveExactAsync(Socket s, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReceiveAsync(buf.AsMemory(read), SocketFlags.None, ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }
}

public sealed record ReverseSnapshot(
    bool Running,
    int LinkPort,
    int LocalPort,
    int Ready,              // connections from the sharing device that are waiting for a request
    int ActiveConnections,
    long SpeedDown,         // bytes per second, internet -> this device
    long SpeedUp,
    long TotalDown,
    long TotalUp);

// Receiving side of the reverse connection. It waits on a "link port" for the sharing device
// (which dials in and keeps several connections ready) and opens a normal proxy on 127.0.0.1.
// Every connection made to that local proxy is passed, byte for byte, through one ready
// connection; the sharing device answers it with its own proxy code (SOCKS5 / HTTP).
public sealed class ReverseReceiver : IDisposable
{
    const string Tag = "Reverse";
    const int MaxIdle = 32;
    static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan WaitForLink = TimeSpan.FromSeconds(10);

    readonly object lifecycle = new();
    readonly object idleLock = new();
    readonly List<Socket> idle = new();
    readonly SemaphoreSlim idleSignal = new(0);
    readonly object liveLock = new();
    readonly HashSet<Socket> live = new();

    TcpListener? linkListener;
    TcpListener? localListener;
    CancellationTokenSource? cts;
    System.Threading.Timer? timer;
    string secret = "";
    bool running;
    int linkPort, localPort;
    int active;
    int logBudget;
    long totalDown, totalUp, lastDown, lastUp, speedDown, speedUp, lastTick;

    public bool IsRunning => running;
    public int LinkPort => linkPort;
    public int LocalPort => localPort;

    // About once a second while running (thread-pool thread) and once more on Stop.
    public event Action<ReverseSnapshot>? StatsChanged;

    // Throws SocketException when a port is already in use.
    public void Start(int link, int local, string pairSecret)
    {
        if (link is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(link));
        if (local is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(local));
        if (link == local) throw new ArgumentException("The two ports must differ");

        lock (lifecycle)
        {
            if (running) throw new InvalidOperationException("Already running");

            var linkL = new TcpListener(IPAddress.Any, link);
            try
            {
                linkL.Start(64);
            }
            catch (Exception ex)
            {
                AppLog.Error(Tag, $"cannot listen on link port {link}", ex);
                throw;
            }
            var localL = new TcpListener(IPAddress.Loopback, local);
            try
            {
                localL.Start(128);
            }
            catch (Exception ex)
            {
                try { linkL.Stop(); } catch { }
                AppLog.Error(Tag, $"cannot listen on local proxy port {local}", ex);
                throw;
            }

            secret = pairSecret ?? "";
            linkPort = link;
            localPort = local;
            Interlocked.Exchange(ref active, 0);
            Interlocked.Exchange(ref logBudget, 0);
            Interlocked.Exchange(ref totalDown, 0);
            Interlocked.Exchange(ref totalUp, 0);
            lastDown = lastUp = speedDown = speedUp = 0;
            lastTick = Stopwatch.GetTimestamp();

            cts = new CancellationTokenSource();
            linkListener = linkL;
            localListener = localL;
            running = true;
            var token = cts.Token;
            _ = Task.Run(() => LinkAcceptLoopAsync(linkL, token));
            _ = Task.Run(() => LocalAcceptLoopAsync(localL, token));
            timer = new System.Threading.Timer(OnTick, null, 1000, 1000);
            AppLog.Write(Tag, $"receiving: link port {link}, local proxy 127.0.0.1:{local} (pairing password={(secret.Length > 0 ? "on" : "off")})");
        }
    }

    public void Stop()
    {
        lock (lifecycle)
        {
            if (!running) return;
            running = false;
            try { cts?.Cancel(); } catch { }
            try { linkListener?.Stop(); } catch { }
            try { localListener?.Stop(); } catch { }
            timer?.Dispose();
            timer = null;
            linkListener = null;
            localListener = null;
        }

        List<Socket> toClose;
        lock (idleLock)
        {
            toClose = idle.ToList();
            idle.Clear();
        }
        lock (liveLock)
        {
            toClose.AddRange(live);
            live.Clear();
        }
        foreach (var s in toClose) CloseQuiet(s);
        Interlocked.Exchange(ref speedDown, 0);
        Interlocked.Exchange(ref speedUp, 0);
        AppLog.Write(Tag, "stopped");
        StatsChanged?.Invoke(GetSnapshot());
    }

    public void Dispose() => Stop();

    public ReverseSnapshot GetSnapshot()
    {
        int ready;
        lock (idleLock) ready = idle.Count;
        return new ReverseSnapshot(running, linkPort, localPort, ready, Volatile.Read(ref active),
            Interlocked.Read(ref speedDown), Interlocked.Read(ref speedUp),
            Interlocked.Read(ref totalDown), Interlocked.Read(ref totalUp));
    }

    // ---------------------------------------------------------------- the sharing device dials in

    async Task LinkAcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket sock;
            try
            {
                sock = await l.AcceptSocketAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                AppLog.Error(Tag, "accept on the link port failed", ex);
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            _ = Task.Run(() => PairAsync(sock, ct));
        }
    }

    async Task PairAsync(Socket sock, CancellationToken ct)
    {
        var from = "?";
        var keep = false;
        try
        {
            from = (sock.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            try { sock.NoDelay = true; } catch { }
            try { sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }

            using var pair = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pair.CancelAfter(PairTimeout);
            var challenge = ReverseProtocol.NewChallenge();
            var sent = 0;
            while (sent < challenge.Length)
            {
                var n = await sock.SendAsync(challenge.AsMemory(sent), SocketFlags.None, pair.Token);
                if (n <= 0) throw new IOException("closed while pairing");
                sent += n;
            }
            var reply = new byte[ReverseProtocol.ReplySize];
            await ReverseProtocol.ReceiveExactAsync(sock, reply, pair.Token);
            if (!ReverseProtocol.Verify(secret, challenge, reply))
            {
                AppLog.Write(Tag, $"{from}: pairing refused (wrong password or not a BtChat device)");
                return;
            }

            if (!running) return;
            lock (idleLock)
            {
                if (idle.Count >= MaxIdle)
                {
                    AppLog.Write(Tag, $"{from}: too many ready connections, closed one");
                    return;
                }
                idle.Add(sock);
                keep = true;
            }
            idleSignal.Release();
            LogFew($"{from}: ready connection added");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (OperationCanceledException) { AppLog.Write(Tag, $"{from}: pairing timed out"); }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
        {
            AppLog.Verbose(Tag, $"{from}: pairing failed ({ex.GetType().Name})");
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, $"pairing with {from} failed", ex);
        }
        finally
        {
            if (!keep) CloseQuiet(sock);
        }
    }

    static bool IsAlive(Socket s)
    {
        try { return !(s.Poll(0, SelectMode.SelectRead) && s.Available == 0); }
        catch { return false; }
    }

    // Newest ready connection that is still open; null when none shows up in time.
    async Task<Socket?> TakeAsync(CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(WaitForLink);
        try
        {
            while (true)
            {
                await idleSignal.WaitAsync(wait.Token);
                Socket? candidate = null;
                lock (idleLock)
                {
                    if (idle.Count > 0)
                    {
                        candidate = idle[^1];
                        idle.RemoveAt(idle.Count - 1);
                    }
                }
                if (candidate == null) continue;
                if (IsAlive(candidate)) return candidate;
                CloseQuiet(candidate);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- the local proxy

    async Task LocalAcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket app;
            try
            {
                app = await l.AcceptSocketAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                AppLog.Error(Tag, "accept on the local proxy port failed", ex);
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            _ = Task.Run(() => HandleLocalAsync(app, ct));
        }
    }

    async Task HandleLocalAsync(Socket app, CancellationToken ct)
    {
        Socket? link = null;
        var counted = false;
        try
        {
            try { app.NoDelay = true; } catch { }
            link = await TakeAsync(ct);
            if (link == null)
            {
                if (!ct.IsCancellationRequested) LogFew("a request came but no sharing device is linked");
                return;
            }
            Interlocked.Increment(ref active);
            counted = true;
            lock (liveLock)
            {
                live.Add(app);
                live.Add(link);
            }
            await Task.WhenAll(
                PumpAsync(app, link, true, ct),     // from the app towards the internet
                PumpAsync(link, app, false, ct));   // from the internet back to the app
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error(Tag, "local connection failed", ex);
        }
        finally
        {
            if (counted) Interlocked.Decrement(ref active);
            lock (liveLock)
            {
                live.Remove(app);
                if (link != null) live.Remove(link);
            }
            CloseQuiet(link);
            CloseQuiet(app);
        }
    }

    // Copies one direction until it ends. Never throws; on an error both sockets are closed.
    async Task PumpAsync(Socket src, Socket dst, bool up, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (true)
            {
                var n = await src.ReceiveAsync(buf.AsMemory(), SocketFlags.None, ct);
                if (n == 0)
                {
                    try { dst.Shutdown(SocketShutdown.Send); } catch { }
                    return;
                }
                var sent = 0;
                while (sent < n)
                {
                    var w = await dst.SendAsync(buf.AsMemory(sent, n - sent), SocketFlags.None, ct);
                    if (w <= 0) throw new IOException("socket closed while sending");
                    sent += w;
                }
                if (up) Interlocked.Add(ref totalUp, n);
                else Interlocked.Add(ref totalDown, n);
            }
        }
        catch
        {
            CloseQuiet(src);
            CloseQuiet(dst);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // ---------------------------------------------------------------- housekeeping

    void OnTick(object? _)
    {
        var now = Stopwatch.GetTimestamp();
        var secs = Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds;
        if (secs < 0.2) secs = 1;
        lastTick = now;
        var down = Interlocked.Read(ref totalDown);
        var up = Interlocked.Read(ref totalUp);
        Interlocked.Exchange(ref speedDown, (long)((down - lastDown) / secs));
        Interlocked.Exchange(ref speedUp, (long)((up - lastUp) / secs));
        lastDown = down;
        lastUp = up;

        // ready connections the sharing device closed meanwhile (stopped, network change ...)
        List<Socket>? dead = null;
        lock (idleLock)
        {
            for (var i = idle.Count - 1; i >= 0; i--)
            {
                if (IsAlive(idle[i])) continue;
                (dead ??= new List<Socket>()).Add(idle[i]);
                idle.RemoveAt(i);
            }
        }
        if (dead != null) foreach (var s in dead) CloseQuiet(s);

        try { StatsChanged?.Invoke(GetSnapshot()); }
        catch (Exception ex) { AppLog.Error(Tag, "stats listener failed", ex); }
    }

    void LogFew(string message)
    {
        if (Interlocked.Increment(ref logBudget) <= 60) AppLog.Write(Tag, message);
    }

    static void CloseQuiet(Socket? s)
    {
        if (s == null) return;
        try { s.Dispose(); } catch { }
    }
}
