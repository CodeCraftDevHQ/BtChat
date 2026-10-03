using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace BtChat;

public sealed class ChatSession : IDisposable
{
    const byte FrameText = 1;
    const byte FramePing = 5;
    // File frames carry a 4-byte file id first, so several files can be in flight at once.
    const byte FrameFileOffer = 8;  // [id][size i64][name]  announces a queued file
    const byte FrameFileBegin = 9;  // [id]                  the sender starts sending it now
    const byte FrameFileChunk = 10; // [id][bytes]
    const byte FrameFileEnd = 11;   // [id]
    const byte FrameFileCancel = 12; // [id]                 sender gave up (queued or running)

    readonly Stream stream;
    readonly IReceivedFileStore store;
    readonly string name;
    readonly SemaphoreSlim writeLock = new(1, 1);
    int nextFileId;
    long lastReceived = Environment.TickCount64;
    int framesRead;

    public event Action<ChatMessage>? MessageReceived;

    public ChatSession(Stream stream, IReceivedFileStore store, string name)
    {
        this.stream = stream;
        this.store = store;
        this.name = name;
    }

    public Task SendTextAsync(string text, CancellationToken ct = default)
    {
        AppLog.Write("SESSION", $"{name} text tx chars={text.Length}");
        return WriteFrameAsync(FrameText, Encoding.UTF8.GetBytes(text), ct);
    }

    public uint NewFileId() => (uint)Interlocked.Increment(ref nextFileId);

    // Tells the other side a file is waiting in the queue, so it can show it right away.
    public Task OfferFileAsync(uint id, string fileName, long size, CancellationToken ct = default)
    {
        AppLog.Write("SESSION", $"{name} file offer id={id} {fileName} size={size}");
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        var payload = new byte[8 + nameBytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload, size);
        nameBytes.CopyTo(payload, 8);
        return WriteFrameAsync(FrameFileOffer, id, payload, ct);
    }

    public async Task SendFileAsync(uint id, Stream source, Action<long, long>? onProgress = null, long size = -1, CancellationToken ct = default)
    {
        AppLog.Write("SESSION", $"{name} file tx start id={id}");
        ct.ThrowIfCancellationRequested();
        await WriteFrameAsync(FrameFileBegin, id, ReadOnlyMemory<byte>.Empty, ct);
        onProgress?.Invoke(0, size);
        var buffer = new byte[Protocol.ChunkSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await WriteFrameAsync(FrameFileChunk, id, buffer.AsMemory(0, read), ct);
            total += read;
            onProgress?.Invoke(total, size);
        }
        await WriteFrameAsync(FrameFileEnd, id, ReadOnlyMemory<byte>.Empty, ct);
        AppLog.Write("SESSION", $"{name} file tx done id={id} bytes={total}");
    }

    // Safe to call for any id (queued, running or already gone): the receiver ignores unknown ids.
    public Task CancelFileAsync(uint id)
    {
        AppLog.Write("SESSION", $"{name} file cancel id={id}");
        return WriteFrameAsync(FrameFileCancel, id, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
    }

    Task WriteFrameAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        WriteFrameAsync(type, null, payload, ct);

    async Task WriteFrameAsync(byte type, uint? id, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            var header = new byte[id.HasValue ? 9 : 5];
            header[0] = type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length + (id.HasValue ? 4 : 0));
            if (id.HasValue) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5), id.Value);
            // ct only guards waiting for the lock: cancelling in the middle of a frame would corrupt the stream.
            await stream.WriteAsync(header, CancellationToken.None);
            if (payload.Length > 0) await stream.WriteAsync(payload, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        AppLog.Write("SESSION", $"{name} read loop started");
        Interlocked.Exchange(ref lastReceived, Environment.TickCount64);
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => WatchdogAsync(heartbeat.Token));
        _ = Task.Run(() => PingAsync(heartbeat.Token));
        var header = new byte[5];
        var incoming = new Dictionary<uint, IncomingFile>();
        try
        {
            while (true)
            {
                await stream.ReadExactlyAsync(header, ct);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
                if (length < 0 || length > Protocol.MaxFrame)
                    throw new InvalidDataException($"bad frame type={header[0]} length={length}");
                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, ct);
                Interlocked.Exchange(ref lastReceived, Environment.TickCount64);
                framesRead++;

                var type = header[0];
                switch (type)
                {
                    case FramePing:
                        AppLog.Write("SESSION", $"{name} ping rx");
                        break;
                    case FrameText:
                        AppLog.Write("SESSION", $"{name} text rx bytes={length}");
                        MessageReceived?.Invoke(new ChatMessage { Text = Encoding.UTF8.GetString(payload) });
                        break;
                    case FrameFileOffer:
                    {
                        if (payload.Length < 12) break;
                        var id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                        var size = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(4));
                        var fileName = Path.GetFileName(Encoding.UTF8.GetString(payload.AsSpan(12)));
                        if (string.IsNullOrWhiteSpace(fileName)) fileName = "file";
                        if (incoming.Remove(id, out var stale))
                        {
                            await AbortFileAsync(stale.File);
                            stale.Message.Fail();
                        }
                        var message = new ChatMessage { Text = fileName, IsFile = true };
                        message.SetQueued("fileWaiting");
                        incoming[id] = new IncomingFile(fileName, size, message);
                        AppLog.Write("SESSION", $"{name} file rx offer id={id} {fileName} size={size}");
                        MessageReceived?.Invoke(message);
                        break;
                    }
                    case FrameFileBegin:
                    {
                        if (payload.Length < 4 || !incoming.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(payload), out var item)) break;
                        item.File = await store.CreateAsync(item.Name, ct);
                        AppLog.Write("SESSION", $"{name} file rx start {item.File.Name}");
                        item.Message.Begin(item.File.Name, item.File.Location, item.Expected);
                        break;
                    }
                    case FrameFileChunk:
                    {
                        if (payload.Length < 4 || !incoming.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(payload), out var item) || item.File == null) break;
                        await item.File.Stream.WriteAsync(payload.AsMemory(4), ct);
                        item.Received += payload.Length - 4;
                        item.Message.Report(item.Received, item.Expected);
                        break;
                    }
                    case FrameFileEnd:
                    {
                        if (payload.Length < 4) break;
                        var id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                        if (!incoming.Remove(id, out var item) || item.File == null) break;
                        await item.File.Stream.DisposeAsync();
                        await item.File.Complete();
                        AppLog.Write("SESSION", $"{name} file rx done {item.File.Name}");
                        item.Message.Complete();
                        break;
                    }
                    case FrameFileCancel:
                    {
                        if (payload.Length < 4) break;
                        var id = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                        if (!incoming.Remove(id, out var item)) break;
                        AppLog.Write("SESSION", $"{name} file rx canceled by sender {item.Name}");
                        await AbortFileAsync(item.File);
                        item.Message.Fail("fileCanceledByPeer");
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
            foreach (var item in incoming.Values)
            {
                await AbortFileAsync(item.File);
                item.Message.Fail();
            }
            incoming.Clear();
            AppLog.Write("SESSION", $"{name} read loop ended after {sw.Elapsed.TotalSeconds:F1}s frames={framesRead}");
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
                await WriteFrameAsync(FramePing, ReadOnlyMemory<byte>.Empty, ct);
                AppLog.Write("SESSION", $"{name} ping tx");
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

    sealed class IncomingFile(string name, long expected, ChatMessage message)
    {
        public string Name { get; } = name;
        public long Expected { get; } = expected;
        public ChatMessage Message { get; } = message;
        public ReceivedFile? File { get; set; }
        public long Received { get; set; }
    }

    public void Dispose()
    {
        AppLog.Write("SESSION", $"{name} Dispose called, caller: {AppLog.Caller()}");
        stream.Dispose();
    }
}
