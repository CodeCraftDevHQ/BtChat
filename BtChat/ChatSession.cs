using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BtChat;

public sealed class ChatSession : IDisposable
{
    const byte FrameText = 1;
    const byte FrameHello = 2;
    const byte FramePing = 5;
    // File frames carry a 4-byte file id first, so several files can be in flight at once.
    const byte FrameFileOffer = 8;  // [id][size i64][key 16][name]  announces a queued file
    const byte FrameFileBegin = 9;  // [id]                          the sender wants to start it now
    const byte FrameFileChunk = 10; // [id][bytes]
    const byte FrameFileEnd = 11;   // [id]
    const byte FrameFileCancel = 12; // [id]                         sender gave up (queued or running)
    const byte FrameFileAccept = 13; // [id][offset i64]             receiver -> sender: start sending from this byte
    const byte FrameFileRetry = 14;  // [key 16]                     receiver -> sender: please send this file again
    const byte FrameFileRetryDenied = 15; // [key 16]                sender -> receiver: that file is not available

    // type(1) + length(4) + file id(4)
    const int FileHeaderSize = 9;
    const int OfferFixedSize = 4 + 8 + 16;

    readonly Stream stream;
    readonly IReceivedFileStore store;
    readonly ResumeRegistry resumes;
    readonly string name;
    readonly int chunkSize;
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly ConcurrentDictionary<uint, TaskCompletionSource<long>> accepts = new();
    int nextFileId;
    long lastReceived = Environment.TickCount64;
    int framesRead;

    public event Action<ChatMessage>? MessageReceived;
    public event Action? PeerIdentified;
    // The other side asked to get a file again (it is identified by its transfer key).
    public event Action<Guid>? RetryRequested;

    public string? PeerId { get; private set; }
    public string? PeerName { get; private set; }
    public volatile string? ReceiveFolder;

    public ChatSession(Stream stream, IReceivedFileStore store, string name, ResumeRegistry resumes)
    {
        this.stream = stream;
        this.store = store;
        this.name = name;
        this.resumes = resumes;
        // Wi-Fi moves big frames efficiently; Bluetooth is slow, so small frames keep text and pings responsive.
        chunkSize = name.StartsWith("tcp", StringComparison.Ordinal) ? Protocol.TcpChunkSize : Protocol.BluetoothChunkSize;
    }

    public Task SendTextAsync(string text, CancellationToken ct = default)
    {
        AppLog.Write("SESSION", $"{name} text tx chars={text.Length}");
        return WriteFrameAsync(FrameText, null, Encoding.UTF8.GetBytes(text), ct);
    }

    public Task SendHelloAsync(CancellationToken ct = default)
    {
        AppLog.Write("SESSION", $"{name} hello tx");
        return WriteFrameAsync(FrameHello, null, Encoding.UTF8.GetBytes(LocalDevice.Id + "\n" + LocalDevice.Name), ct);
    }

    public uint NewFileId() => (uint)Interlocked.Increment(ref nextFileId);

    // Tells the other side a file is waiting in the queue, so it can show it right away.
    // The key identifies the file across connections, so an interrupted transfer can continue later.
    public Task OfferFileAsync(uint id, string fileName, long size, Guid key, CancellationToken ct = default)
    {
        AppLog.Write("SESSION", $"{name} file offer id={id} {fileName} size={size} key={key:N}");
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        var payload = new byte[8 + 16 + nameBytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload, size);
        key.TryWriteBytes(payload.AsSpan(8, 16));
        nameBytes.CopyTo(payload, 24);
        return WriteFrameAsync(FrameFileOffer, id, payload, ct);
    }

    public Task RequestRetryAsync(Guid key)
    {
        AppLog.Write("SESSION", $"{name} retry request tx key={key:N}");
        return WriteFrameAsync(FrameFileRetry, null, key.ToByteArray(), CancellationToken.None);
    }

    public Task DenyRetryAsync(Guid key)
    {
        AppLog.Write("SESSION", $"{name} retry denied tx key={key:N}");
        return WriteFrameAsync(FrameFileRetryDenied, null, key.ToByteArray(), CancellationToken.None);
    }

    public async Task SendFileAsync(uint id, Stream source, Action<long, long>? onProgress = null, long size = -1, CancellationToken ct = default)
    {
        AppLog.Write("SESSION", $"{name} file tx start id={id}");
        ct.ThrowIfCancellationRequested();
        var accepted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        accepts[id] = accepted;
        byte[]? first = null;
        byte[]? second = null;
        Task<int>? pending = null;
        try
        {
            await WriteFrameAsync(FrameFileBegin, id, ReadOnlyMemory<byte>.Empty, ct);
            // The receiver tells us how many bytes it already has (0 for a new file).
            var offset = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(Protocol.AcceptTimeoutSeconds), ct);
            if (offset < 0) offset = 0;
            AppLog.Write("SESSION", $"{name} file tx accepted id={id} offset={offset}");
            if (offset > 0) await SkipAsync(source, offset, ct);
            onProgress?.Invoke(offset, size);

            // Two buffers: while one chunk is written to the connection the next one is already read from the file.
            first = ArrayPool<byte>.Shared.Rent(FileHeaderSize + chunkSize);
            second = ArrayPool<byte>.Shared.Rent(FileHeaderSize + chunkSize);
            var current = first;
            var spare = second;
            long total = offset;
            var read = await ReadFullAsync(source, current, chunkSize, ct);
            while (read > 0)
            {
                pending = ReadFullAsync(source, spare, chunkSize, ct);
                WriteChunkHeader(current, id, read);
                await WriteRawAsync(current.AsMemory(0, FileHeaderSize + read), ct);
                total += read;
                onProgress?.Invoke(total, size);
                read = await pending;
                pending = null;
                (current, spare) = (spare, current);
            }
            await WriteFrameAsync(FrameFileEnd, id, ReadOnlyMemory<byte>.Empty, ct);
            AppLog.Write("SESSION", $"{name} file tx done id={id} bytes={total - offset} (from offset {offset})");
        }
        finally
        {
            accepts.TryRemove(id, out _);
            // A read may still be running into the spare buffer: wait for it before the buffers go back to the pool.
            if (pending != null)
            {
                try { await pending; } catch { }
            }
            if (first != null) ArrayPool<byte>.Shared.Return(first);
            if (second != null) ArrayPool<byte>.Shared.Return(second);
        }
    }

    static void WriteChunkHeader(byte[] buffer, uint id, int count)
    {
        buffer[0] = FrameFileChunk;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1), count + 4);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(5), id);
    }

    // Fills the chunk area of the buffer as much as possible, so frames are full-sized except the last one.
    static async Task<int> ReadFullAsync(Stream source, byte[] buffer, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var read = await source.ReadAsync(buffer.AsMemory(FileHeaderSize + total, count - total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    static async Task SkipAsync(Stream source, long count, CancellationToken ct)
    {
        if (source.CanSeek)
        {
            source.Seek(count, SeekOrigin.Begin);
            return;
        }
        var scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (count > 0)
            {
                var read = await source.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, count)), ct);
                if (read == 0) throw new EndOfStreamException("file is shorter than the part already received");
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    // Safe to call for any id (queued, running or already gone): the receiver ignores unknown ids.
    public Task CancelFileAsync(uint id)
    {
        AppLog.Write("SESSION", $"{name} file cancel id={id}");
        return WriteFrameAsync(FrameFileCancel, id, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
    }

    // Header and payload leave in ONE write: two small writes per chunk cost a packet and a system call each.
    async Task WriteFrameAsync(byte type, uint? id, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var headerSize = id.HasValue ? FileHeaderSize : 5;
        var frame = new byte[headerSize + payload.Length];
        frame[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length + (id.HasValue ? 4 : 0));
        if (id.HasValue) BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), id.Value);
        payload.CopyTo(frame.AsMemory(headerSize));
        await WriteRawAsync(frame, ct);
    }

    async Task WriteRawAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            // ct only guards waiting for the lock: cancelling in the middle of a frame would corrupt the stream.
            await stream.WriteAsync(frame, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
        }
        finally
        {
            writeLock.Release();
        }
    }

    // The read loop must never wait for a write: if both sides are sending big files and both read loops
    // blocked on writing, nobody would read any more and the connection would freeze. Replies go out from here.
    void SendInBackground(byte type, uint? id, byte[] payload)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await WriteFrameAsync(type, id, payload, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLog.Error("SESSION", $"{name} background write failed, closing", ex);
                stream.Dispose();
            }
        });
    }

    static uint ReadId(ReadOnlyMemory<byte> payload) => BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

    static long ReadSize(ReadOnlyMemory<byte> payload) => BinaryPrimitives.ReadInt64LittleEndian(payload.Span[4..]);

    static Guid ReadOfferKey(ReadOnlyMemory<byte> payload) => new(payload.Span.Slice(12, 16));

    static Guid ReadKey(ReadOnlyMemory<byte> payload) => new(payload.Span[..16]);

    static string ReadOfferName(ReadOnlyMemory<byte> payload) => Encoding.UTF8.GetString(payload.Span[OfferFixedSize..]);

    public async Task RunAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        AppLog.Write("SESSION", $"{name} read loop started chunk={chunkSize}");
        Interlocked.Exchange(ref lastReceived, Environment.TickCount64);
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => WatchdogAsync(heartbeat.Token));
        _ = Task.Run(() => PingAsync(heartbeat.Token));
        var header = new byte[5];
        // One buffer is reused for every frame (each frame is fully handled before the next one is read).
        var buffer = new byte[64 * 1024];
        var incoming = new Dictionary<uint, IncomingFile>();
        try
        {
            await SendHelloAsync(ct);
            while (true)
            {
                await stream.ReadExactlyAsync(header, ct);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
                if (length < 0 || length > Protocol.MaxFrame)
                    throw new InvalidDataException($"bad frame type={header[0]} length={length}");
                if (length > buffer.Length) buffer = new byte[Math.Max(length, Math.Min(buffer.Length * 2, Protocol.MaxFrame))];
                var payload = buffer.AsMemory(0, length);
                await stream.ReadExactlyAsync(payload, ct);
                Interlocked.Exchange(ref lastReceived, Environment.TickCount64);
                framesRead++;

                var type = header[0];
                switch (type)
                {
                    case FrameHello:
                    {
                        var text = Encoding.UTF8.GetString(payload.Span);
                        var split = text.IndexOf('\n');
                        if (split <= 0) break;
                        var peerId = text[..split].Trim();
                        var peerName = LocalDevice.Clean(text[(split + 1)..]);
                        if (peerId.Length == 0 || peerId.Length > 64) break;
                        if (peerName.Length == 0) peerName = "Unknown";
                        PeerId = peerId;
                        PeerName = peerName;
                        AppLog.Write("SESSION", $"{name} hello rx id={peerId} name={peerName}");
                        PeerIdentified?.Invoke();
                        break;
                    }
                    case FramePing:
                        AppLog.Verbose("SESSION", $"{name} ping rx");
                        break;
                    case FrameText:
                        AppLog.Write("SESSION", $"{name} text rx bytes={length}");
                        MessageReceived?.Invoke(new ChatMessage { Text = Encoding.UTF8.GetString(payload.Span), SenderName = PeerName ?? "" });
                        break;
                    case FrameFileOffer:
                    {
                        if (payload.Length < OfferFixedSize) break;
                        var id = ReadId(payload);
                        var size = ReadSize(payload);
                        var key = ReadOfferKey(payload);
                        var fileName = Path.GetFileName(ReadOfferName(payload));
                        if (string.IsNullOrWhiteSpace(fileName)) fileName = "file";
                        if (incoming.Remove(id, out var stale))
                        {
                            await DiscardAsync(stale);
                            stale.Message.Fail("fileFailed", keepPartial: false);
                        }
                        AppLog.Write("SESSION", $"{name} file rx offer id={id} {fileName} size={size} key={key:N}");
                        if (key != Guid.Empty && resumes.TryGet(key, out var previous))
                        {
                            // The sender offers a file that failed before: continue in the same chat bubble.
                            var resume = previous;
                            var message = previous.Message;
                            if (previous.Location != null && previous.Expected != size)
                            {
                                // The file changed on the sender's side, so what we already have is useless.
                                AppLog.Write("SESSION", $"{name} file rx size changed ({previous.Expected} -> {size}), dropping the partial file");
                                try { await store.DeleteAsync(previous.Location); }
                                catch (Exception ex) { AppLog.Error("SESSION", "delete stale partial failed", ex); }
                                message.DropPartial();
                                resume = new FailedReceive { Key = key, Message = message, Expected = size, Location = null };
                            }
                            message.SetOffered(size);
                            message.Revive("fileWaiting");
                            incoming[id] = new IncomingFile(fileName, size, message, key, resume);
                        }
                        else
                        {
                            var message = new ChatMessage { Text = fileName, IsFile = true, SenderName = PeerName ?? "", TransferKey = key };
                            message.SetOffered(size);
                            message.SetQueued("fileWaiting");
                            incoming[id] = new IncomingFile(fileName, size, message, key, null);
                            MessageReceived?.Invoke(message);
                        }
                        break;
                    }
                    case FrameFileBegin:
                    {
                        if (payload.Length < 4 || !incoming.TryGetValue(ReadId(payload), out var item)) break;
                        await BeginReceiveAsync(ReadId(payload), item, ct);
                        break;
                    }
                    case FrameFileChunk:
                    {
                        if (payload.Length < 4 || !incoming.TryGetValue(ReadId(payload), out var item) || item.File == null) break;
                        await item.File.Stream.WriteAsync(payload[4..], ct);
                        item.Received += payload.Length - 4;
                        item.Message.Report(item.Received, item.Expected);
                        break;
                    }
                    case FrameFileEnd:
                    {
                        if (payload.Length < 4) break;
                        var id = ReadId(payload);
                        if (!incoming.Remove(id, out var item) || item.File == null) break;
                        await item.File.Stream.DisposeAsync();
                        await item.File.Complete();
                        if (item.Expected > 0 && item.Received != item.Expected)
                            AppLog.Write("SESSION", $"{name} file rx size differs: expected={item.Expected} got={item.Received}");
                        AppLog.Write("SESSION", $"{name} file rx done {item.File.Name}");
                        item.Message.Complete();
                        break;
                    }
                    case FrameFileCancel:
                    {
                        if (payload.Length < 4) break;
                        var id = ReadId(payload);
                        if (!incoming.Remove(id, out var item)) break;
                        AppLog.Write("SESSION", $"{name} file rx canceled by sender {item.Name}");
                        await DiscardAsync(item);
                        Register(item, null);
                        item.Message.Fail("fileCanceledByPeer", keepPartial: false);
                        break;
                    }
                    case FrameFileAccept:
                    {
                        if (payload.Length < 12) break;
                        var id = ReadId(payload);
                        var offset = BinaryPrimitives.ReadInt64LittleEndian(payload.Span[4..]);
                        if (accepts.TryGetValue(id, out var waiting)) waiting.TrySetResult(offset);
                        break;
                    }
                    case FrameFileRetry:
                    {
                        if (payload.Length < 16) break;
                        var key = ReadKey(payload);
                        AppLog.Write("SESSION", $"{name} retry request rx key={key:N}");
                        RetryRequested?.Invoke(key);
                        break;
                    }
                    case FrameFileRetryDenied:
                    {
                        if (payload.Length < 16) break;
                        var key = ReadKey(payload);
                        AppLog.Write("SESSION", $"{name} retry denied rx key={key:N}");
                        if (resumes.TryGet(key, out var failed)) failed.Message.ShowNote("retryDenied");
                        break;
                    }
                    default:
                        AppLog.Write("SESSION", $"{name} unknown frame type={type}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("SESSION", $"{name} read loop error after {sw.Elapsed.TotalSeconds:F1}s frames={framesRead}", ex);
            throw;
        }
        finally
        {
            heartbeat.Cancel();
            foreach (var waiting in accepts.Values) waiting.TrySetException(new IOException("connection closed"));
            foreach (var item in incoming.Values) await InterruptAsync(item);
            incoming.Clear();
            AppLog.Write("SESSION", $"{name} read loop ended after {sw.Elapsed.TotalSeconds:F1}s frames={framesRead}");
        }
    }

    // The sender starts a file: open the partial one (continue) or a brand new one, and tell the sender where to begin.
    async Task BeginReceiveAsync(uint id, IncomingFile item, CancellationToken ct)
    {
        ReceivedFile? file = null;
        long offset = 0;
        var partial = item.Resume?.Location;
        if (partial != null)
        {
            try
            {
                file = await store.OpenForResumeAsync(partial, ct);
            }
            catch (Exception ex)
            {
                AppLog.Error("SESSION", $"{name} cannot reopen partial file", ex);
            }
            if (file != null && item.Expected > 0 && file.ExistingLength > item.Expected)
            {
                await AbortFileAsync(file);
                file = null;
            }
            if (file != null) offset = file.ExistingLength;
            else AppLog.Write("SESSION", $"{name} partial file unusable, starting from the beginning");
        }
        file ??= await store.CreateAsync(ReceiveFolder ?? LocalDevice.SafeFolder(PeerName), item.Name, ct);
        item.File = file;
        item.Received = offset;
        if (item.Key != Guid.Empty) resumes.Remove(item.Key);
        AppLog.Write("SESSION", $"{name} file rx start {file.Name} from byte {offset}");
        item.Message.Begin(file.Name, file.Location, item.Expected, offset);
        var reply = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(reply, offset);
        SendInBackground(FrameFileAccept, id, reply);
    }

    void Register(IncomingFile item, string? location)
    {
        if (item.Key == Guid.Empty) return;
        resumes.Set(new FailedReceive { Key = item.Key, Message = item.Message, Expected = item.Expected, Location = location });
    }

    // The connection broke while the file was not finished: keep what arrived, so it can be continued.
    async Task InterruptAsync(IncomingFile item)
    {
        if (item.File != null && item.Received > 0)
        {
            try { await item.File.Stream.DisposeAsync(); }
            catch (Exception ex) { AppLog.Error("SESSION", "closing partial file failed", ex); }
            Register(item, item.File.Location);
            item.Message.Fail("fileInterrupted", keepPartial: true);
            return;
        }
        var oldPartial = item.Resume?.Location;
        if (item.File == null && oldPartial != null)
        {
            // Offered again but not started yet: the older partial file is still there.
            Register(item, oldPartial);
            item.Message.Fail("fileInterrupted", keepPartial: true);
            return;
        }
        await AbortFileAsync(item.File);
        Register(item, null);
        item.Message.Fail("fileFailed", keepPartial: false);
    }

    // The sender gave up: the partial file is not wanted any more.
    async Task DiscardAsync(IncomingFile item)
    {
        await AbortFileAsync(item.File);
        var oldPartial = item.Resume?.Location;
        if (item.File == null && oldPartial != null)
        {
            try { await store.DeleteAsync(oldPartial); }
            catch (Exception ex) { AppLog.Error("SESSION", "delete old partial failed", ex); }
        }
    }

    async Task WatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(3000, ct);
                var silent = Environment.TickCount64 - Interlocked.Read(ref lastReceived);
                if (silent > Protocol.PingTimeoutMs)
                {
                    AppLog.Write("SESSION", $"{name} watchdog timeout silent={silent}ms, closing");
                    stream.Dispose();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task PingAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(Protocol.PingIntervalMs, ct);
                await WriteFrameAsync(FramePing, null, ReadOnlyMemory<byte>.Empty, ct);
                AppLog.Verbose("SESSION", $"{name} ping tx");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("SESSION", $"{name} ping tx failed, closing", ex);
            stream.Dispose();
        }
    }

    static async Task AbortFileAsync(ReceivedFile? file)
    {
        if (file == null) return;
        try
        {
            await file.Stream.DisposeAsync();
            await file.Abort();
        }
        catch (Exception ex)
        {
            AppLog.Error("SESSION", "abort partial file failed", ex);
        }
    }

    sealed class IncomingFile(string name, long expected, ChatMessage message, Guid key, FailedReceive? resume)
    {
        public string Name { get; } = name;
        public long Expected { get; } = expected;
        public ChatMessage Message { get; } = message;
        public Guid Key { get; } = key;
        public FailedReceive? Resume { get; } = resume;
        public ReceivedFile? File { get; set; }
        public long Received { get; set; }
    }

    public void Dispose()
    {
        AppLog.Write("SESSION", $"{name} Dispose called, caller: {AppLog.Caller()}");
        stream.Dispose();
    }
}
