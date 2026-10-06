using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace BtChat;

// Settings of one proxy session. Limits can also be changed while running (ProxyServer.UpdateLimits).
public sealed class ProxyOptions
{
    public const int DefaultPort = 8080;

    public int Port { get; set; } = DefaultPort;

    // Optional security: leave Username empty for an open proxy.
    public string? Username { get; set; }
    public string? Password { get; set; }

    // Only accept clients that sit on a local network (192.168.x.x, 10.x.x.x, hotspot ...).
    public bool LocalNetworkOnly { get; set; } = true;

    // Clients may not reach this device itself (127.0.0.1 or its own addresses) through the proxy.
    public bool BlockLocalTargets { get; set; } = true;

    // Data caps in bytes (download + upload). 0 = unlimited.
    public long TotalLimitBytes { get; set; }
    public long PerClientLimitBytes { get; set; }

    public bool AuthRequired => !string.IsNullOrEmpty(Username);

    public ProxyOptions Clone() => (ProxyOptions)MemberwiseClone();
}

// Down = internet -> client, Up = client -> internet.
public sealed record ProxyClientInfo(string Address, int Connections, long BytesDown, long BytesUp, bool LimitReached);

public sealed record ProxySnapshot(
    bool Running,
    int Port,
    int ActiveConnections,
    int ActiveClients,
    long SpeedDown,          // bytes per second
    long SpeedUp,            // bytes per second
    long TotalDown,          // bytes since Start / ResetCounters
    long TotalUp,
    long TotalLimitBytes,
    long PerClientLimitBytes,
    bool TotalLimitReached,
    IReadOnlyList<ProxyClientInfo> Clients);

// Plain TCP proxy that speaks HTTP (plain + CONNECT for https) and SOCKS5 (CONNECT) on a single port.
// Outgoing connections use the normal route of this device, so when a VPN is active here the
// clients' traffic goes through the VPN too.
public sealed class ProxyServer : IDisposable
{
    const string Tag = "Proxy";
    const int MaxConnections = 512;
    const int MaxHeadBytes = 32 * 1024;
    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    sealed class ClientState
    {
        public string Address = "";
        public int Connections;
        public long Down;
        public long Up;
        public bool LimitReached;
        public readonly List<Socket> Sockets = new();
        public long Used => Down + Up;
    }

    enum LimitHit { None, Client, Total }

    sealed record Handshake(Socket Target, byte[]? Prefix);

    sealed class ProxyDeniedException : Exception
    {
        public ProxyDeniedException(string message) : base(message) { }
    }

    readonly object lifecycle = new();
    readonly object statsLock = new();
    readonly Dictionary<string, ClientState> clients = new();

    ProxyOptions options = new();
    TcpListener? listener;
    CancellationTokenSource? cts;
    System.Threading.Timer? timer;
    bool running;
    int logBudget;
    long selfTestUntil;
    HashSet<IPAddress> selfTestAddresses = new();

    // Guarded by statsLock.
    int activeConnections;
    long totalDown, totalUp, lastDown, lastUp, speedDown, speedUp;
    long totalLimit, perClientLimit;
    bool totalLimitReached;
    long lastTick;

    readonly object ownLock = new();
    HashSet<IPAddress> ownAddresses = new();
    DateTime ownAddressesAt = DateTime.MinValue;

    public bool IsRunning => running;
    public int Port => options.Port;

    // Raised about once a second while running (from a thread-pool thread) and once more on Stop.
    public event Action<ProxySnapshot>? StatsChanged;

    // ---------------------------------------------------------------- lifecycle

    // Throws SocketException when the port is already in use.
    public void Start(ProxyOptions o)
    {
        if (o.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(o), "Port must be 1-65535");
        if (o.Username != null && o.Username.Contains(':')) throw new ArgumentException("User name must not contain ':'", nameof(o));

        lock (lifecycle)
        {
            if (running) throw new InvalidOperationException("Proxy is already running");

            var l = new TcpListener(IPAddress.Any, o.Port);
            try
            {
                l.Start(128);
            }
            catch (Exception ex)
            {
                AppLog.Error(Tag, $"cannot listen on port {o.Port}", ex);
                throw;
            }

            AppLog.Write(Tag, $"listening on {l.LocalEndpoint}");
            options = o.Clone();
            Interlocked.Exchange(ref logBudget, 0);
            lock (statsLock)
            {
                clients.Clear();
                activeConnections = 0;
                totalDown = totalUp = lastDown = lastUp = speedDown = speedUp = 0;
                totalLimit = options.TotalLimitBytes;
                perClientLimit = options.PerClientLimitBytes;
                totalLimitReached = false;
                lastTick = Stopwatch.GetTimestamp();
            }

            cts = new CancellationTokenSource();
            listener = l;
            running = true;
            var token = cts.Token;
            _ = Task.Run(() => AcceptLoopAsync(l, token));
            _ = Task.Run(() => SelfTestAsync(o.Port, token));
            timer = new System.Threading.Timer(OnTick, null, 1000, 1000);
            AppLog.Write(Tag, $"started on port {options.Port} (auth={(options.AuthRequired ? "on" : "off")}, localOnly={options.LocalNetworkOnly}, totalLimit={totalLimit}, perClientLimit={perClientLimit})");
        }
    }

    public void Stop()
    {
        lock (lifecycle)
        {
            if (!running) return;
            running = false;
            try { cts?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            timer?.Dispose();
            timer = null;
            listener = null;
        }
        CloseAll();
        lock (statsLock)
        {
            speedDown = speedUp = 0;
        }
        AppLog.Write(Tag, "stopped");
        StatsChanged?.Invoke(GetSnapshot());
    }

    public void Dispose() => Stop();

    // Zeroes the counters and lifts a reached data cap (the cap itself stays).
    public void ResetCounters()
    {
        lock (statsLock)
        {
            totalDown = totalUp = lastDown = lastUp = speedDown = speedUp = 0;
            totalLimitReached = false;
            foreach (var key in clients.Where(kv => kv.Value.Connections == 0).Select(kv => kv.Key).ToList())
                clients.Remove(key);
            foreach (var c in clients.Values)
            {
                c.Down = c.Up = 0;
                c.LimitReached = false;
            }
        }
        AppLog.Write(Tag, "counters reset");
        if (running) StatsChanged?.Invoke(GetSnapshot());
    }

    // Changes the caps while running (0 = unlimited). Lowering a cap below what was already used
    // cuts the affected connections right away.
    public void UpdateLimits(long totalLimitBytes, long perClientLimitBytes)
    {
        bool closeAll = false;
        var closeClients = new List<ClientState>();
        lock (statsLock)
        {
            totalLimit = Math.Max(0, totalLimitBytes);
            perClientLimit = Math.Max(0, perClientLimitBytes);
            totalLimitReached = totalLimit > 0 && totalDown + totalUp >= totalLimit;
            closeAll = totalLimitReached;
            foreach (var c in clients.Values)
            {
                var reached = perClientLimit > 0 && c.Used >= perClientLimit;
                if (reached && !c.LimitReached) closeClients.Add(c);
                c.LimitReached = reached;
            }
        }
        AppLog.Write(Tag, $"limits changed: total={totalLimit}, perClient={perClientLimit}");
        if (closeAll) CloseAll();
        else foreach (var c in closeClients) CloseClient(c);
    }

    public ProxySnapshot GetSnapshot()
    {
        lock (statsLock)
        {
            var list = clients.Values
                .Where(c => c.Connections > 0 || c.Used > 0)
                .OrderByDescending(c => c.Connections).ThenByDescending(c => c.Used)
                .Select(c => new ProxyClientInfo(c.Address, c.Connections, c.Down, c.Up, c.LimitReached))
                .ToList();
            return new ProxySnapshot(
                running, options.Port, activeConnections,
                clients.Values.Count(c => c.Connections > 0),
                speedDown, speedUp, totalDown, totalUp,
                totalLimit, perClientLimit, totalLimitReached, list);
        }
    }

    void OnTick(object? _)
    {
        lock (statsLock)
        {
            var now = Stopwatch.GetTimestamp();
            var secs = Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds;
            if (secs < 0.2) secs = 1;
            lastTick = now;
            speedDown = (long)((totalDown - lastDown) / secs);
            speedUp = (long)((totalUp - lastUp) / secs);
            lastDown = totalDown;
            lastUp = totalUp;

            // forget clients that are gone and never moved any data
            foreach (var key in clients.Where(kv => kv.Value.Connections == 0 && kv.Value.Used == 0).Select(kv => kv.Key).ToList())
                clients.Remove(key);
        }
        try { StatsChanged?.Invoke(GetSnapshot()); }
        catch (Exception ex) { AppLog.Error(Tag, "stats listener failed", ex); }
    }

    // ---------------------------------------------------------------- accepting

    async Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await l.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                AppLog.Error(Tag, "accept failed", ex);
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            try
            {
                LogFew($"accepted {tcp.Client.RemoteEndPoint} on {tcp.Client.LocalEndPoint}");
            }
            catch
            {
            }
            _ = Task.Run(() => HandleConnectionAsync(tcp, ct));
        }
    }

    void LogFew(string message)
    {
        if (Interlocked.Increment(ref logBudget) <= 120) AppLog.Write(Tag, message);
    }

    async Task SelfTestAsync(int port, CancellationToken ct)
    {
        try
        {
            await Task.Delay(700, ct);
            var targets = new List<IPAddress> { IPAddress.Loopback };
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !targets.Contains(ua.Address))
                        targets.Add(ua.Address);
            }
            selfTestAddresses = new HashSet<IPAddress>(targets);
            Interlocked.Exchange(ref selfTestUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 20);
            foreach (var ip in targets)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    using var client = new TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(3000);
                    await client.ConnectAsync(ip, port, timeout.Token);
                    AppLog.Write(Tag, $"self-test {ip}:{port} ok in {watch.ElapsedMilliseconds} ms");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var code = ex is SocketException se ? se.SocketErrorCode.ToString() : ex.GetType().Name;
                    AppLog.Write(Tag, $"self-test {ip}:{port} FAILED after {watch.ElapsedMilliseconds} ms: {code}");
                }
            }
            Interlocked.Exchange(ref selfTestUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "self-test crashed", ex);
        }
    }

    // Returns null when the connection is accepted, otherwise the reason it was rejected.
    string? TryRegister(IPAddress ip, Socket sock, out ClientState? state)
    {
        state = null;
        var addr = ip.ToString();
        var isNew = false;
        string? rejected = null;
        lock (statsLock)
        {
            if (activeConnections >= MaxConnections) rejected = "too many connections";
            else if (totalLimitReached) rejected = "data limit reached";
            else
            {
                if (!clients.TryGetValue(addr, out var c))
                {
                    c = new ClientState { Address = addr };
                    clients[addr] = c;
                    isNew = true;
                }
                if (c.LimitReached) rejected = "client data limit reached";
                else
                {
                    c.Connections++;
                    activeConnections++;
                    c.Sockets.Add(sock);
                    state = c;
                }
            }
        }
        if (isNew) AppLog.Write(Tag, $"new client {addr}");
        return rejected;
    }

    void AddSocket(ClientState cs, Socket s)
    {
        lock (statsLock) cs.Sockets.Add(s);
    }

    void Unregister(ClientState cs, params Socket?[] sockets)
    {
        lock (statsLock)
        {
            cs.Connections = Math.Max(0, cs.Connections - 1);
            activeConnections = Math.Max(0, activeConnections - 1);
            foreach (var s in sockets)
                if (s != null) cs.Sockets.Remove(s);
        }
    }

    async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        ClientState? cs = null;
        Socket? clientSock = null;
        Socket? target = null;
        var addr = "?";
        try
        {
            clientSock = tcp.Client;
            var ip = (clientSock.RemoteEndPoint as IPEndPoint)?.Address;
            if (ip == null) return;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            addr = ip.ToString();

            if (Stopwatch.GetTimestamp() < Interlocked.Read(ref selfTestUntil) &&
                (IPAddress.IsLoopback(ip) || selfTestAddresses.Contains(ip)))
            {
                AppLog.Write(Tag, $"self-test connection accepted from {addr}");
                return;
            }

            if (options.LocalNetworkOnly && !IsLocalNetwork(ip))
            {
                AppLog.Write(Tag, $"rejected {addr}: not a local network address");
                return;
            }
            var reason = TryRegister(ip, clientSock, out cs);
            if (reason != null)
            {
                AppLog.Verbose(Tag, $"rejected {addr}: {reason}");
                return;
            }

            tcp.NoDelay = true;
            try { clientSock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
            var stream = tcp.GetStream();

            Handshake? hs;
            using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hsCts.CancelAfter(HandshakeTimeout);
                var first = new byte[1];
                await ReadExactAsync(stream, first, 1, hsCts.Token);
                LogFew($"{addr}: first byte 0x{first[0]:X2} ({(first[0] == 0x05 ? "SOCKS5" : "HTTP")})");
                hs = first[0] == 0x05
                    ? await Socks5Async(stream, addr, hsCts.Token)
                    : await HttpAsync(stream, first[0], addr, hsCts.Token);
            }
            if (hs == null)
            {
                LogFew($"{addr}: handshake refused");
                return;
            }

            target = hs.Target;
            AddSocket(cs!, target);
            try { target.NoDelay = true; target.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }

            if (hs.Prefix is { Length: > 0 } prefix)
            {
                if (!await ForwardAsync(target, prefix, prefix.Length, cs!, true, ct)) return;
            }

            await Task.WhenAll(
                PumpAsync(clientSock, target, cs!, true, ct),
                PumpAsync(target, clientSock, cs!, false, ct));
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) LogFew($"{addr}: timed out");
        }
        catch (EndOfStreamException) { LogFew($"{addr}: closed before the handshake finished"); }
        catch (IOException ex) { LogFew($"{addr}: {ex.GetType().Name}: {ex.Message}"); }
        catch (SocketException ex) { AppLog.Verbose(Tag, $"{addr}: socket {ex.SocketErrorCode}"); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { AppLog.Error(Tag, $"connection from {addr} failed", ex); }
        finally
        {
            if (cs != null) Unregister(cs, clientSock, target);
            CloseQuiet(target);
            try { tcp.Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------- SOCKS5

    async Task<Handshake?> Socks5Async(NetworkStream s, string addr, CancellationToken ct)
    {
        var b = new byte[262];

        // method selection (the version byte 0x05 was already read)
        await ReadExactAsync(s, b, 1, ct);
        int methodCount = b[0];
        await ReadExactAsync(s, b, methodCount, ct);
        bool noAuth = false, userPass = false;
        for (int i = 0; i < methodCount; i++)
        {
            if (b[i] == 0x00) noAuth = true;
            else if (b[i] == 0x02) userPass = true;
        }
        byte method;
        if (options.AuthRequired) method = userPass ? (byte)0x02 : (byte)0xFF;
        else method = noAuth ? (byte)0x00 : (byte)0xFF;
        await s.WriteAsync(new byte[] { 0x05, method }, ct);
        if (method == 0xFF)
        {
            AppLog.Verbose(Tag, $"{addr}: SOCKS5 no acceptable auth method");
            return null;
        }

        // RFC 1929 user/password
        if (method == 0x02)
        {
            await ReadExactAsync(s, b, 1, ct);
            if (b[0] != 0x01) return null;
            await ReadExactAsync(s, b, 1, ct);
            var userBytes = new byte[b[0]];
            await ReadExactAsync(s, userBytes, userBytes.Length, ct);
            await ReadExactAsync(s, b, 1, ct);
            var passBytes = new byte[b[0]];
            await ReadExactAsync(s, passBytes, passBytes.Length, ct);
            if (!CheckCredentials(Encoding.UTF8.GetString(userBytes), Encoding.UTF8.GetString(passBytes)))
            {
                AppLog.Write(Tag, $"{addr}: SOCKS5 wrong user name or password");
                await Task.Delay(1000, ct);
                await s.WriteAsync(new byte[] { 0x01, 0x01 }, ct);
                return null;
            }
            await s.WriteAsync(new byte[] { 0x01, 0x00 }, ct);
        }

        // request: VER CMD RSV ATYP DST.ADDR DST.PORT
        await ReadExactAsync(s, b, 4, ct);
        if (b[0] != 0x05) return null;
        byte cmd = b[1], atyp = b[3];
        string host;
        switch (atyp)
        {
            case 0x01:
                await ReadExactAsync(s, b, 4, ct);
                host = new IPAddress(b.AsSpan(0, 4)).ToString();
                break;
            case 0x03:
                await ReadExactAsync(s, b, 1, ct);
                int len = b[0];
                await ReadExactAsync(s, b, len, ct);
                host = Encoding.UTF8.GetString(b, 0, len);
                break;
            case 0x04:
                await ReadExactAsync(s, b, 16, ct);
                host = new IPAddress(b.AsSpan(0, 16)).ToString();
                break;
            default:
                await Socks5ReplyAsync(s, 0x08, ct);
                return null;
        }
        await ReadExactAsync(s, b, 2, ct);
        int port = (b[0] << 8) | b[1];

        if (cmd != 0x01)
        {
            // BIND and UDP ASSOCIATE are not supported
            await Socks5ReplyAsync(s, 0x07, ct);
            return null;
        }

        LogFew($"{addr}: SOCKS5 CONNECT {host}:{port}");
        Socket target;
        try
        {
            target = await ConnectTargetAsync(host, port, ct);
        }
        catch (ProxyDeniedException ex)
        {
            AppLog.Write(Tag, $"{addr}: blocked {host}:{port} ({ex.Message})");
            await Socks5ReplyAsync(s, 0x02, ct);
            return null;
        }
        catch (SocketException ex)
        {
            LogFew($"{addr}: connect {host}:{port} failed: {ex.SocketErrorCode}");
            await Socks5ReplyAsync(s, MapSocksError(ex.SocketErrorCode), ct);
            return null;
        }
        await Socks5ReplyAsync(s, 0x00, ct);
        return new Handshake(target, null);
    }

    static byte MapSocksError(SocketError e) => e switch
    {
        SocketError.ConnectionRefused => 0x05,
        SocketError.TimedOut => 0x06,
        SocketError.NetworkUnreachable => 0x03,
        SocketError.HostUnreachable => 0x04,
        SocketError.HostNotFound => 0x04,
        SocketError.TryAgain => 0x04,
        SocketError.NoData => 0x04,
        _ => 0x01,
    };

    static Task Socks5ReplyAsync(Stream s, byte code, CancellationToken ct) =>
        s.WriteAsync(new byte[] { 0x05, code, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct).AsTask();

    // ---------------------------------------------------------------- HTTP / HTTPS

    async Task<Handshake?> HttpAsync(NetworkStream s, byte firstByte, string addr, CancellationToken ct)
    {
        var buf = new byte[MaxHeadBytes];
        buf[0] = firstByte;
        int len = 1;
        int headEnd;
        while ((headEnd = FindHeadEnd(buf, len)) < 0)
        {
            if (len == buf.Length)
            {
                await HttpErrorAsync(s, 431, "Request Header Fields Too Large", ct);
                return null;
            }
            int n = await s.ReadAsync(buf.AsMemory(len), ct);
            if (n == 0) return null;
            len += n;
        }

        var leftover = buf.AsSpan(headEnd, len - headEnd).ToArray();
        var lines = Encoding.Latin1.GetString(buf, 0, headEnd - 4).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            await HttpErrorAsync(s, 400, "Bad Request", ct);
            return null;
        }
        string method = parts[0], requestTarget = parts[1], version = parts[2];

        string? proxyAuth = null;
        bool upgrade = false;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            var name = lines[i][..colon].Trim();
            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) proxyAuth = lines[i][(colon + 1)..].Trim();
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) upgrade = true;
        }

        if (options.AuthRequired)
        {
            if (proxyAuth == null)
            {
                await HttpProxyAuthRequiredAsync(s, ct);
                return null;
            }
            if (!TryParseBasic(proxyAuth, out var user, out var pass) || !CheckCredentials(user, pass))
            {
                AppLog.Write(Tag, $"{addr}: HTTP wrong user name or password");
                await Task.Delay(1000, ct);
                await HttpProxyAuthRequiredAsync(s, ct);
                return null;
            }
        }

        string host;
        int port;
        bool isConnect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
        Uri? uri = null;
        if (isConnect)
        {
            if (!TryParseHostPort(requestTarget, out host, out port))
            {
                await HttpErrorAsync(s, 400, "Bad Request", ct);
                return null;
            }
        }
        else
        {
            if (!Uri.TryCreate(requestTarget, UriKind.Absolute, out uri) ||
                !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                await HttpErrorAsync(s, 400, "Bad Request (this is a proxy; use an absolute http:// URL)", ct);
                return null;
            }
            host = uri.DnsSafeHost;
            port = uri.Port;
        }

        AppLog.Verbose(Tag, $"{addr}: HTTP {(isConnect ? "CONNECT" : method)} {host}:{port}");
        Socket target;
        try
        {
            target = await ConnectTargetAsync(host, port, ct);
        }
        catch (ProxyDeniedException ex)
        {
            AppLog.Write(Tag, $"{addr}: blocked {host}:{port} ({ex.Message})");
            await HttpErrorAsync(s, 403, "Forbidden", ct);
            return null;
        }
        catch (SocketException ex)
        {
            AppLog.Verbose(Tag, $"{addr}: connect {host}:{port} failed: {ex.SocketErrorCode}");
            if (ex.SocketErrorCode == SocketError.TimedOut) await HttpErrorAsync(s, 504, "Gateway Timeout", ct);
            else await HttpErrorAsync(s, 502, "Bad Gateway", ct);
            return null;
        }

        if (isConnect)
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
            return new Handshake(target, leftover.Length > 0 ? leftover : null);
        }

        // Plain HTTP: turn the proxy-style request into a normal one for the origin server.
        // One request per connection (Connection: close) keeps the relay simple, except for upgrades (WebSocket).
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(uri!.PathAndQuery).Append(' ').Append(version).Append("\r\n");
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                var name = lines[i][..colon].Trim();
                if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
                if (!upgrade && (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))) continue;
            }
            if (lines[i].Length == 0) continue;
            sb.Append(lines[i]).Append("\r\n");
        }
        if (!upgrade) sb.Append("Connection: close\r\n");
        sb.Append("\r\n");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        var prefix = new byte[head.Length + leftover.Length];
        Buffer.BlockCopy(head, 0, prefix, 0, head.Length);
        Buffer.BlockCopy(leftover, 0, prefix, head.Length, leftover.Length);
        return new Handshake(target, prefix);
    }

    static int FindHeadEnd(byte[] buf, int len)
    {
        for (int i = 3; i < len; i++)
            if (buf[i] == '\n' && buf[i - 1] == '\r' && buf[i - 2] == '\n' && buf[i - 3] == '\r')
                return i + 1;
        return -1;
    }

    static bool TryParseBasic(string headerValue, out string user, out string pass)
    {
        user = pass = "";
        const string prefix = "Basic ";
        if (!headerValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue[prefix.Length..].Trim()));
            int colon = decoded.IndexOf(':');
            if (colon < 0) return false;
            user = decoded[..colon];
            pass = decoded[(colon + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    static bool TryParseHostPort(string text, out string host, out int port)
    {
        host = "";
        port = 0;
        string portText;
        if (text.StartsWith('['))
        {
            int end = text.IndexOf(']');
            if (end < 0 || end + 1 >= text.Length || text[end + 1] != ':') return false;
            host = text[1..end];
            portText = text[(end + 2)..];
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon <= 0) return false;
            host = text[..colon];
            portText = text[(colon + 1)..];
        }
        return int.TryParse(portText, out port) && port is >= 1 and <= 65535;
    }

    static Task HttpErrorAsync(Stream s, int code, string text, CancellationToken ct)
    {
        var body = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {text}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        return s.WriteAsync(body, ct).AsTask();
    }

    static Task HttpProxyAuthRequiredAsync(Stream s, CancellationToken ct)
    {
        var body = Encoding.ASCII.GetBytes(
            "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"BtChat\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        return s.WriteAsync(body, ct).AsTask();
    }

    // ---------------------------------------------------------------- outgoing connection

    async Task<Socket> ConnectTargetAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = new[] { literal };
        else addresses = await Dns.GetHostAddressesAsync(host, ct);

        // IPv4 first: many VPN setups have no working IPv6 route.
        var ordered = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
        if (ordered.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

        Exception? last = null;
        bool anyAllowed = false;
        foreach (var address in ordered)
        {
            if (options.BlockLocalTargets && IsBlockedTarget(address)) continue;
            anyAllowed = true;
            var sock = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                using var cc = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cc.CancelAfter(ConnectTimeout);
                await sock.ConnectAsync(new IPEndPoint(address, port), cc.Token);
                return sock;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sock.Dispose();
                last = new SocketException((int)SocketError.TimedOut);
            }
            catch (Exception ex)
            {
                sock.Dispose();
                if (ct.IsCancellationRequested) throw;
                last = ex;
            }
        }
        if (!anyAllowed) throw new ProxyDeniedException("target is this device or a reserved address");
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    // ---------------------------------------------------------------- relay + data accounting

    // Copies one direction until EOF. Never throws; on an error both sockets are closed.
    async Task PumpAsync(Socket src, Socket dst, ClientState cs, bool up, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (true)
            {
                int n = await src.ReceiveAsync(buf.AsMemory(), SocketFlags.None, ct);
                if (n == 0)
                {
                    // this side finished sending; let the other direction finish
                    try { dst.Shutdown(SocketShutdown.Send); } catch { }
                    return;
                }
                if (!await ForwardAsync(dst, buf, n, cs, up, ct))
                {
                    CloseQuiet(src);
                    CloseQuiet(dst);
                    return;
                }
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

    // Counts the bytes against the caps and sends what is still allowed. False = stop this connection.
    async Task<bool> ForwardAsync(Socket dst, byte[] data, int count, ClientState cs, bool up, CancellationToken ct)
    {
        var (allowed, hit) = Reserve(cs, count, up);
        if (allowed > 0) await SendAllAsync(dst, data, allowed, ct);
        if (hit == LimitHit.Total) CloseAll();
        else if (hit == LimitHit.Client) CloseClient(cs);
        return allowed == count && hit == LimitHit.None;
    }

    (int Allowed, LimitHit Hit) Reserve(ClientState cs, int count, bool up)
    {
        string? message = null;
        int allowed;
        var hit = LimitHit.None;
        lock (statsLock)
        {
            long room = long.MaxValue;
            if (totalLimit > 0) room = Math.Min(room, totalLimit - (totalDown + totalUp));
            if (perClientLimit > 0) room = Math.Min(room, perClientLimit - cs.Used);
            if (room <= 0) return (0, LimitHit.None);   // limit was hit earlier; the connection is being closed already

            allowed = (int)Math.Min(count, room);
            if (up) { cs.Up += allowed; totalUp += allowed; }
            else { cs.Down += allowed; totalDown += allowed; }

            if (totalLimit > 0 && totalDown + totalUp >= totalLimit)
            {
                if (!totalLimitReached) message = $"total data limit reached ({totalDown + totalUp} bytes)";
                totalLimitReached = true;
                hit = LimitHit.Total;
            }
            else if (perClientLimit > 0 && cs.Used >= perClientLimit)
            {
                if (!cs.LimitReached) message = $"data limit of client {cs.Address} reached ({cs.Used} bytes)";
                cs.LimitReached = true;
                hit = LimitHit.Client;
            }
        }
        if (message != null) AppLog.Write(Tag, message);
        return (allowed, hit);
    }

    static async Task SendAllAsync(Socket s, byte[] data, int count, CancellationToken ct)
    {
        int sent = 0;
        while (sent < count)
        {
            int n = await s.SendAsync(data.AsMemory(sent, count - sent), SocketFlags.None, ct);
            if (n <= 0) throw new IOException("socket closed while sending");
            sent += n;
        }
    }

    // ---------------------------------------------------------------- closing

    void CloseAll()
    {
        List<Socket> all;
        lock (statsLock)
        {
            all = clients.Values.SelectMany(c => c.Sockets).ToList();
        }
        foreach (var s in all) CloseQuiet(s);
    }

    void CloseClient(ClientState cs)
    {
        List<Socket> list;
        lock (statsLock) list = cs.Sockets.ToList();
        foreach (var s in list) CloseQuiet(s);
    }

    static void CloseQuiet(Socket? s)
    {
        if (s == null) return;
        try { s.Dispose(); } catch { }
    }

    static async Task ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    // ---------------------------------------------------------------- security helpers

    bool CheckCredentials(string? user, string? pass)
    {
        if (!options.AuthRequired) return true;
        if (user == null || pass == null) return false;
        var given = Encoding.UTF8.GetBytes(user + "\0" + pass);
        var expected = Encoding.UTF8.GetBytes(options.Username + "\0" + options.Password);
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    // Private / link-local ranges: hotspot, home Wi-Fi, Ethernet.
    public static bool IsLocalNetwork(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
        return false;
    }

    bool IsBlockedTarget(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
            return true;
        if (ip.IsIPv6Multicast) return true;
        return GetOwnAddresses().Contains(ip);
    }

    HashSet<IPAddress> GetOwnAddresses()
    {
        lock (ownLock)
        {
            if (DateTime.UtcNow - ownAddressesAt < TimeSpan.FromSeconds(10)) return ownAddresses;
            var set = new HashSet<IPAddress>();
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        foreach (var a in n.GetIPProperties().UnicastAddresses)
                        {
                            var ip = a.Address;
                            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                            set.Add(ip);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(Tag, "reading own addresses failed", ex);
            }
            ownAddresses = set;
            ownAddressesAt = DateTime.UtcNow;
            return set;
        }
    }
}
