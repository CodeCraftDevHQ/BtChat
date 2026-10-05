using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace BtChat.Tun;

public sealed class TunOptions
{
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public uint DnsAddress { get; set; } = 0xC6120002;
    public string[] UpstreamDns { get; set; } = { "8.8.8.8", "1.1.1.1" };
    public int Mtu { get; set; } = 1400;
    public int MaxFlows { get; set; } = 3000;
}

public interface ITunIo
{
    int Read(byte[] buffer);
    void Write(byte[] buffer, int offset, int count);
}

internal readonly record struct FlowKey(uint SrcIp, ushort SrcPort, uint DstIp, ushort DstPort);

public sealed class TunEngine : IDisposable
{
    const string Tag = "VPN";

    internal readonly TunOptions Options;
    readonly ITunIo io;
    readonly object writeLock = new();
    readonly ConcurrentDictionary<FlowKey, TcpFlow> flows = new();
    readonly ConcurrentDictionary<string, (byte[] Data, long Expires)> dnsCache = new();
    readonly SemaphoreSlim dnsGate = new(24);
    readonly CancellationTokenSource cts = new();
    Thread? reader;
    Timer? timer;
    int ipId;
    long bytesDown;
    long bytesUp;
    int disposed;

    public TunEngine(ITunIo io, TunOptions options)
    {
        this.io = io;
        Options = options;
    }

    public long BytesDown => Interlocked.Read(ref bytesDown);
    public long BytesUp => Interlocked.Read(ref bytesUp);
    public int ActiveFlows => flows.Count;
    public int Mss => Options.Mtu - 40;
    public event Action<string>? Stopped;

    internal CancellationToken Token => cts.Token;
    internal void AddDown(int n) => Interlocked.Add(ref bytesDown, n);
    internal void AddUp(int n) => Interlocked.Add(ref bytesUp, n);

    public void Start()
    {
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "tun-read" };
        reader.Start();
        timer = new Timer(_ => Tick(), null, 500, 500);
    }

    void ReadLoop()
    {
        var buf = new byte[65536];
        var reason = "stopped";
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var n = io.Read(buf);
                if (n < 0)
                {
                    reason = "tun closed";
                    break;
                }
                if (n == 0) continue;
                try
                {
                    HandlePacket(buf, n);
                }
                catch (Exception ex)
                {
                    AppLog.Error(Tag, "packet failed", ex);
                }
            }
        }
        catch (Exception ex)
        {
            reason = "read failed: " + ex.Message;
            if (!cts.IsCancellationRequested) AppLog.Error(Tag, "tun read failed", ex);
        }
        if (!cts.IsCancellationRequested) Stopped?.Invoke(reason);
    }

    void Tick()
    {
        try
        {
            var now = Environment.TickCount64;
            foreach (var flow in flows.Values) flow.Tick(now);
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "timer failed", ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cts.Cancel();
        try { timer?.Dispose(); } catch { }
        foreach (var flow in flows.Values) flow.Abort(false);
        flows.Clear();
        if (reader != null && reader != Thread.CurrentThread)
        {
            try { reader.Join(1500); } catch { }
        }
    }

    internal static IPAddress ToAddress(uint ip)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, ip);
        return new IPAddress(b);
    }

    internal void Remove(FlowKey key) => flows.TryRemove(key, out _);

    void HandlePacket(byte[] b, int n)
    {
        if (n < 20 || (b[0] >> 4) != 4) return;
        var ihl = (b[0] & 15) * 4;
        if (ihl < 20 || n < ihl) return;
        int total = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(2));
        if (total < ihl || total > n) return;
        if ((BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(6)) & 0x3FFF) != 0) return;
        var src = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12));
        var dst = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16));
        var payload = b.AsSpan(ihl, total - ihl);
        switch (b[9])
        {
            case 6:
                HandleTcp(src, dst, payload);
                break;
            case 17:
                HandleUdp(src, dst, payload, b.AsSpan(0, Math.Min(total, ihl + 8)));
                break;
        }
    }

    void HandleTcp(uint src, uint dst, ReadOnlySpan<byte> p)
    {
        if (p.Length < 20) return;
        var off = (p[12] >> 4) * 4;
        if (off < 20 || off > p.Length) return;
        var sport = BinaryPrimitives.ReadUInt16BigEndian(p);
        var dport = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        var seq = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
        var ack = BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
        var flags = p[13];
        var window = BinaryPrimitives.ReadUInt16BigEndian(p[14..]);
        var data = p[off..];
        var key = new FlowKey(src, sport, dst, dport);

        if (flows.TryGetValue(key, out var flow))
        {
            flow.OnSegment(flags, seq, ack, window, data);
            return;
        }
        if ((flags & TcpFlags.Rst) != 0) return;

        if ((flags & TcpFlags.Syn) != 0 && (flags & TcpFlags.Ack) == 0)
        {
            if (flows.Count >= Options.MaxFlows || dst == Options.DnsAddress)
            {
                SendTcp(dst, dport, src, sport, 0, seq + 1, TcpFlags.Rst | TcpFlags.Ack, 0, default);
                return;
            }
            var mss = 536;
            var opts = p[20..off];
            for (var i = 0; i < opts.Length;)
            {
                var kind = opts[i];
                if (kind == 0) break;
                if (kind == 1)
                {
                    i++;
                    continue;
                }
                if (i + 1 >= opts.Length) break;
                var len = opts[i + 1];
                if (len < 2 || i + len > opts.Length) break;
                if (kind == 2 && len == 4) mss = BinaryPrimitives.ReadUInt16BigEndian(opts[(i + 2)..]);
                i += len;
            }
            var created = new TcpFlow(this, key, seq, window, mss);
            if (flows.TryAdd(key, created)) created.Start();
            return;
        }

        if ((flags & TcpFlags.Ack) != 0)
            SendTcp(dst, dport, src, sport, ack, 0, TcpFlags.Rst, 0, default);
        else
        {
            var len = (uint)data.Length + ((flags & TcpFlags.Syn) != 0 ? 1u : 0u) + ((flags & TcpFlags.Fin) != 0 ? 1u : 0u);
            SendTcp(dst, dport, src, sport, 0, seq + len, TcpFlags.Rst | TcpFlags.Ack, 0, default);
        }
    }

    void HandleUdp(uint src, uint dst, ReadOnlySpan<byte> p, ReadOnlySpan<byte> original)
    {
        if (p.Length < 8) return;
        var sport = BinaryPrimitives.ReadUInt16BigEndian(p);
        var dport = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        int len = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        if (len < 8 || len > p.Length) return;
        if (dport == 53)
        {
            var query = p[8..len].ToArray();
            _ = Task.Run(() => HandleDnsAsync(src, sport, dst, query));
            return;
        }
        SendIcmpPortUnreachable(src, dst, original);
    }

    internal void SendTcp(uint srcIp, ushort srcPort, uint dstIp, ushort dstPort, uint seq, uint ack, byte flags, ushort window, ReadOnlySpan<byte> payload, ushort mss = 0)
    {
        var optLen = mss != 0 ? 4 : 0;
        var tcpLen = 20 + optLen + payload.Length;
        var total = 20 + tcpLen;
        var buf = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var span = buf.AsSpan(0, total);
            span[..(40 + optLen)].Clear();
            WriteIpHeader(span, total, 6, srcIp, dstIp);
            var t = span[20..];
            BinaryPrimitives.WriteUInt16BigEndian(t, srcPort);
            BinaryPrimitives.WriteUInt16BigEndian(t[2..], dstPort);
            BinaryPrimitives.WriteUInt32BigEndian(t[4..], seq);
            BinaryPrimitives.WriteUInt32BigEndian(t[8..], ack);
            t[12] = (byte)(((20 + optLen) / 4) << 4);
            t[13] = flags;
            BinaryPrimitives.WriteUInt16BigEndian(t[14..], window);
            if (mss != 0)
            {
                t[20] = 2;
                t[21] = 4;
                BinaryPrimitives.WriteUInt16BigEndian(t[22..], mss);
            }
            payload.CopyTo(t[(20 + optLen)..]);
            var sum = PseudoSum(srcIp, dstIp, 6, tcpLen) + Sum(t[..tcpLen]);
            BinaryPrimitives.WriteUInt16BigEndian(t[16..], Fold(sum));
            WriteOut(buf, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    internal void SendUdp(uint srcIp, ushort srcPort, uint dstIp, ushort dstPort, ReadOnlySpan<byte> payload)
    {
        var udpLen = 8 + payload.Length;
        var total = 20 + udpLen;
        var buf = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var span = buf.AsSpan(0, total);
            span[..28].Clear();
            WriteIpHeader(span, total, 17, srcIp, dstIp);
            var u = span[20..];
            BinaryPrimitives.WriteUInt16BigEndian(u, srcPort);
            BinaryPrimitives.WriteUInt16BigEndian(u[2..], dstPort);
            BinaryPrimitives.WriteUInt16BigEndian(u[4..], (ushort)udpLen);
            payload.CopyTo(u[8..]);
            var sum = PseudoSum(srcIp, dstIp, 17, udpLen) + Sum(u[..udpLen]);
            var cs = Fold(sum);
            BinaryPrimitives.WriteUInt16BigEndian(u[6..], cs == 0 ? (ushort)0xFFFF : cs);
            WriteOut(buf, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    void SendIcmpPortUnreachable(uint clientIp, uint targetIp, ReadOnlySpan<byte> original)
    {
        var icmpLen = 8 + original.Length;
        var total = 20 + icmpLen;
        var buf = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var span = buf.AsSpan(0, total);
            span[..28].Clear();
            WriteIpHeader(span, total, 1, targetIp, clientIp);
            var c = span[20..];
            c[0] = 3;
            c[1] = 3;
            original.CopyTo(c[8..]);
            BinaryPrimitives.WriteUInt16BigEndian(c[2..], Fold(Sum(c[..icmpLen])));
            WriteOut(buf, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    void WriteIpHeader(Span<byte> span, int total, byte protocol, uint src, uint dst)
    {
        span[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)total);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)Interlocked.Increment(ref ipId));
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], 0x4000);
        span[8] = 64;
        span[9] = protocol;
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], src);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], dst);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], Fold(Sum(span[..20])));
    }

    void WriteOut(byte[] buf, int total)
    {
        if (cts.IsCancellationRequested) return;
        try
        {
            lock (writeLock) io.Write(buf, 0, total);
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) AppLog.Error(Tag, "tun write failed", ex);
        }
    }

    static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)((data[i] << 8) | data[i + 1]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        return sum;
    }

    static uint PseudoSum(uint src, uint dst, byte protocol, int length) =>
        (src >> 16) + (src & 0xFFFF) + (dst >> 16) + (dst & 0xFFFF) + protocol + (uint)length;

    static ushort Fold(uint sum)
    {
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    async Task HandleDnsAsync(uint client, ushort clientPort, uint server, byte[] query)
    {
        if (query.Length < 12 || (query[2] & 0x80) != 0) return;
        var qEnd = QuestionEnd(query);
        if (qEnd < 0) return;
        var qType = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(qEnd - 4));
        byte[]? response;
        if (qType == 28)
        {
            response = Synthesize(query, qEnd, false);
        }
        else
        {
            var cacheKey = Convert.ToBase64String(query.AsSpan(2));
            var now = Environment.TickCount64;
            if (dnsCache.TryGetValue(cacheKey, out var hit) && hit.Expires > now)
            {
                response = (byte[])hit.Data.Clone();
            }
            else
            {
                response = await ResolveAsync(query);
                if (response != null && response.Length >= 12 && (response[3] & 0x0F) == 0 &&
                    BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)) > 0)
                {
                    if (dnsCache.Count > 512) dnsCache.Clear();
                    dnsCache[cacheKey] = ((byte[])response.Clone(), now + 60000);
                }
            }
        }
        if (response == null) return;
        response[0] = query[0];
        response[1] = query[1];
        if (response.Length > Options.Mtu - 28) response = Synthesize(response, QuestionEnd(response), true);
        if (response == null) return;
        SendUdp(server, 53, client, clientPort, response);
    }

    static int QuestionEnd(byte[] m)
    {
        if (m.Length < 12) return -1;
        var i = 12;
        while (i < m.Length)
        {
            var l = m[i];
            if (l == 0)
            {
                i++;
                break;
            }
            if ((l & 0xC0) != 0) return -1;
            i += l + 1;
        }
        i += 4;
        return i <= m.Length ? i : -1;
    }

    static byte[]? Synthesize(byte[] source, int qEnd, bool truncated)
    {
        if (qEnd < 12) return null;
        var r = new byte[qEnd];
        Buffer.BlockCopy(source, 0, r, 0, qEnd);
        r[2] = (byte)(0x80 | (source[2] & 0x79) | (truncated ? 0x02 : 0));
        r[3] = 0x80;
        r[6] = r[7] = r[8] = r[9] = r[10] = r[11] = 0;
        r[5] = 1;
        r[4] = 0;
        return r;
    }

    async Task<byte[]?> ResolveAsync(byte[] query)
    {
        await dnsGate.WaitAsync(cts.Token);
        try
        {
            foreach (var server in Options.UpstreamDns)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    timeout.CancelAfter(8000);
                    using var conn = await Socks5.ConnectAsync(Options.ProxyHost, Options.ProxyPort, Options.User, Options.Password,
                        IPAddress.Parse(server), 53, timeout.Token, 6000);
                    var framed = new byte[query.Length + 2];
                    BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
                    Buffer.BlockCopy(query, 0, framed, 2, query.Length);
                    await conn.Stream.WriteAsync(framed, timeout.Token);
                    var lenBuf = new byte[2];
                    await Socks5.ReadExactAsync(conn.Stream, lenBuf, 2, timeout.Token);
                    var len = BinaryPrimitives.ReadUInt16BigEndian(lenBuf);
                    if (len < 12) continue;
                    var answer = new byte[len];
                    await Socks5.ReadExactAsync(conn.Stream, answer, len, timeout.Token);
                    return answer;
                }
                catch (Exception ex) when (!cts.IsCancellationRequested)
                {
                    AppLog.Write(Tag, $"dns via {server} failed: {ex.Message}");
                }
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            dnsGate.Release();
        }
    }
}

internal static class TcpFlags
{
    public const byte Fin = 1;
    public const byte Syn = 2;
    public const byte Rst = 4;
    public const byte Psh = 8;
    public const byte Ack = 16;
}

internal sealed class TcpFlow
{
    const string Tag = "VPN";
    const int WindowCap = 65535;

    sealed class Unacked(uint seq, byte[] data, bool fin)
    {
        public uint Seq { get; } = seq;
        public byte[] Data { get; } = data;
        public bool Fin { get; } = fin;
        public long Sent { get; set; } = Environment.TickCount64;
        public uint End => Seq + (uint)Data.Length + (Fin ? 1u : 0u);
    }

    enum State { Connecting, Open, Closed }

    readonly TunEngine engine;
    readonly FlowKey key;
    readonly object gate = new();
    readonly CancellationTokenSource cts = new();
    readonly System.Threading.Channels.Channel<byte[]> upload =
        System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    readonly List<Unacked> unacked = new();
    readonly int sendMss;

    State state = State.Connecting;
    uint rcvNxt;
    uint iss;
    uint sndUna;
    uint sndNxt;
    int peerWindow;
    int pending;
    bool clientFin;
    bool finSent;
    bool finAcked;
    int retries;
    long lastActivity = Environment.TickCount64;
    long lastSynAck;
    int synAckCount;
    TaskCompletionSource windowSignal = NewSignal();
    SocksConnection? conn;

    public TcpFlow(TunEngine engine, FlowKey key, uint clientIsn, int window, int clientMss)
    {
        this.engine = engine;
        this.key = key;
        rcvNxt = clientIsn + 1;
        peerWindow = window;
        sendMss = Math.Max(256, Math.Min(clientMss, engine.Mss));
        iss = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
        sndUna = iss;
        sndNxt = iss;
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static bool Lt(uint a, uint b) => (int)(a - b) < 0;
    static bool Le(uint a, uint b) => (int)(a - b) <= 0;

    ushort Window => (ushort)Math.Max(0, WindowCap - pending);

    public void Start() => _ = Task.Run(ConnectAsync);

    async Task ConnectAsync()
    {
        var o = engine.Options;
        SocksConnection c;
        try
        {
            var target = TunEngine.ToAddress(key.DstIp);
            c = await Socks5.ConnectAsync(o.ProxyHost, o.ProxyPort, o.User, o.Password, target, key.DstPort, cts.Token);
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                AppLog.Write(Tag, $"connect {Fmt(key.DstIp)}:{key.DstPort} failed: {ex.Message}");
            lock (gate)
            {
                if (state == State.Closed) return;
                engine.SendTcp(key.DstIp, key.DstPort, key.SrcIp, key.SrcPort, 0, rcvNxt, TcpFlags.Rst | TcpFlags.Ack, 0, default);
                CloseLocked();
            }
            return;
        }
        lock (gate)
        {
            if (state == State.Closed)
            {
                c.Dispose();
                return;
            }
            conn = c;
            state = State.Open;
            sndNxt = iss + 1;
            SendSynAckLocked();
        }
        _ = Task.Run(UploadPump);
        _ = Task.Run(DownloadPump);
    }

    static string Fmt(uint ip) => TunEngine.ToAddress(ip).ToString();

    void SendSynAckLocked()
    {
        lastSynAck = Environment.TickCount64;
        synAckCount++;
        engine.SendTcp(key.DstIp, key.DstPort, key.SrcIp, key.SrcPort, iss, rcvNxt, TcpFlags.Syn | TcpFlags.Ack, Window, default, (ushort)engine.Mss);
    }

    void SendAckLocked() =>
        engine.SendTcp(key.DstIp, key.DstPort, key.SrcIp, key.SrcPort, sndNxt, rcvNxt, TcpFlags.Ack, Window, default);

    void SignalLocked()
    {
        var t = windowSignal;
        windowSignal = NewSignal();
        t.TrySetResult();
    }

    public void OnSegment(byte flags, uint seq, uint ack, ushort window, ReadOnlySpan<byte> payload)
    {
        lock (gate)
        {
            if (state == State.Closed) return;
            lastActivity = Environment.TickCount64;
            if (state == State.Connecting)
            {
                if ((flags & TcpFlags.Rst) != 0) AbortLocked(false);
                return;
            }
            if ((flags & TcpFlags.Rst) != 0)
            {
                AbortLocked(false);
                return;
            }
            if ((flags & TcpFlags.Syn) != 0)
            {
                if (sndUna == iss) SendSynAckLocked();
                return;
            }

            if ((flags & TcpFlags.Ack) != 0)
            {
                peerWindow = window;
                if (Lt(sndUna, ack) && Le(ack, sndNxt))
                {
                    sndUna = ack;
                    retries = 0;
                    unacked.RemoveAll(u => Le(u.End, ack));
                    if (finSent && ack == sndNxt) finAcked = true;
                }
                SignalLocked();
            }

            var fin = (flags & TcpFlags.Fin) != 0;
            if (payload.Length > 0 || fin)
            {
                var segSeq = seq;
                var data = payload;
                var finSeq = seq + (uint)payload.Length;
                var needAck = payload.Length > 0;
                if (Lt(segSeq, rcvNxt))
                {
                    var overlap = rcvNxt - segSeq;
                    data = overlap >= (uint)data.Length ? default : data[(int)overlap..];
                    segSeq = rcvNxt;
                }
                if (segSeq == rcvNxt)
                {
                    if (data.Length > 0)
                    {
                        if (pending + data.Length <= WindowCap)
                        {
                            rcvNxt += (uint)data.Length;
                            pending += data.Length;
                            upload.Writer.TryWrite(data.ToArray());
                            engine.AddUp(data.Length);
                        }
                        needAck = true;
                    }
                    if (fin && finSeq == rcvNxt && !clientFin)
                    {
                        clientFin = true;
                        rcvNxt++;
                        upload.Writer.TryComplete();
                        needAck = true;
                    }
                }
                else
                {
                    needAck = true;
                }
                if (needAck) SendAckLocked();
            }

            if (clientFin && finAcked) CloseLocked();
        }
    }

    async Task UploadPump()
    {
        var c = conn!;
        try
        {
            var reader = upload.Reader;
            while (await reader.WaitToReadAsync(cts.Token))
            {
                while (reader.TryRead(out var chunk))
                {
                    await c.Stream.WriteAsync(chunk, cts.Token);
                    lock (gate)
                    {
                        if (state == State.Closed) return;
                        var before = Window;
                        pending -= chunk.Length;
                        if (before < 32768) SendAckLocked();
                    }
                }
            }
            try { c.Socket.Shutdown(System.Net.Sockets.SocketShutdown.Send); } catch { }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Abort(true);
        }
    }

    async Task DownloadPump()
    {
        var c = conn!;
        var buf = new byte[16384];
        try
        {
            while (true)
            {
                var n = await c.Stream.ReadAsync(buf, cts.Token);
                if (n <= 0) break;
                engine.AddDown(n);
                var off = 0;
                while (off < n)
                {
                    var len = Math.Min(sendMss, n - off);
                    await WaitWindowAsync(len);
                    lock (gate)
                    {
                        if (state == State.Closed) return;
                        var copy = new byte[len];
                        Buffer.BlockCopy(buf, off, copy, 0, len);
                        engine.SendTcp(key.DstIp, key.DstPort, key.SrcIp, key.SrcPort, sndNxt, rcvNxt, TcpFlags.Ack | TcpFlags.Psh, Window, copy);
                        unacked.Add(new Unacked(sndNxt, copy, false));
                        sndNxt += (uint)len;
                    }
                    off += len;
                }
            }
            lock (gate)
            {
                if (state == State.Closed || finSent) return;
                finSent = true;
                engine.SendTcp(key.DstIp, key.DstPort, key.SrcIp, key.SrcPort, sndNxt, rcvNxt, TcpFlags.Fin | TcpFlags.Ack, Window, default);
                unacked.Add(new Unacked(sndNxt, Array.Empty<byte>(), true));
                sndNxt++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Abort(true);
        }
    }

    async Task WaitWindowAsync(int len)
    {
        var waited = 0;
        while (true)
        {
            Task signal;
            lock (gate)
            {
                if (state == State.Closed) throw new OperationCanceledException();
                var inflight = sndNxt - sndUna;
                if (inflight + (uint)len <= (uint)peerWindow) return;
                if (inflight == 0 && waited >= 3) return;
                signal = windowSignal.Task;
            }
            var done = await Task.WhenAny(signal, Task.Delay(500, cts.Token));
            if (done == signal) waited = 0;
            else waited++;
        }
    }

    public void Tick(long now)
    {
        lock (gate)
        {
            if (state == State.Closed) return;
            if (state == State.Connecting) return;
            if (sndUna == iss)
            {
                if (now - lastSynAck > 1500)
                {
                    if (synAckCount > 5) AbortLocked(true);
                    else SendSynAckLocked();
                }
                return;
            }
            if (unacked.Count > 0)
            {
                var u = unacked[0];
                var rto = Math.Min(1000L << Math.Min(retries, 3), 8000);
                if (now - u.Sent > rto)
                {
                    if (retries >= 8)
                    {
                        AbortLocked(true);
                        return;
                    }
                    retries++;
                    u.Sent = now;
                    engine.SendTcp(key.DstIp, key.DstPort, key.SrcIp, key.SrcPort, u.Seq, rcvNxt,
                        u.Fin ? (byte)(TcpFlags.Fin | TcpFlags.Ack) : (byte)(TcpFlags.Ack | TcpFlags.Psh), Window, u.Data);
                }
            }
            var idleLimit = clientFin || finSent ? 60_000 : 20 * 60_000;
            if (now - lastActivity > idleLimit && unacked.Count == 0) AbortLocked(true);
        }
    }

    public void Abort(bool sendRst)
    {
        lock (gate) AbortLocked(sendRst);
    }

    void AbortLocked(bool sendRst)
    {
        if (state == State.Closed) return;
        if (sendRst && state == State.Open)
            engine.SendTcp(key.DstIp, key.DstPort, key.SrcIp, key.SrcPort, sndNxt, rcvNxt, TcpFlags.Rst | TcpFlags.Ack, 0, default);
        CloseLocked();
    }

    void CloseLocked()
    {
        if (state == State.Closed) return;
        state = State.Closed;
        engine.Remove(key);
        upload.Writer.TryComplete();
        SignalLocked();
        try { cts.Cancel(); } catch { }
        conn?.Dispose();
    }
}
