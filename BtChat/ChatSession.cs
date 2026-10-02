using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace BtChat;

public sealed class ChatSession : IDisposable
{
    const byte FrameText = 1;
    const byte FrameFileStart = 2;
    const byte FrameFileChunk = 3;
    const byte FrameFileEnd = 4;
    const byte FramePing = 5;
    const byte FrameFileSize = 6; // sent right after FileStart; old peers ignore it
    const byte FrameFileCancel = 7; // sender aborted the current file; old peers ignore it

    readonly Stream stream;
    readonly IReceivedFileStore store;
    readonly string name;
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly SemaphoreSlim fileLock = new(1, 1);
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

    public async Task SendFileAsync(string fileName, Stream source, Action<long, long>? onProgress = null, CancellationToken ct = default)
    {
        await fileLock.WaitAsync(ct);
        var started = false;
        try
        {
            AppLog.Write("SESSION", $"{name} file tx start {fileName}");
            long size = -1;
            try { if (source.CanSeek) size = source.Length; } catch { }
            ct.ThrowIfCancellationRequested();
            await WriteFrameAsync(FrameFileStart, Encoding.UTF8.GetBytes(fileName), ct);
            started = true;
            if (size >= 0)
            {
                var sizeBytes = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(sizeBytes, size);
                await WriteFrameAsync(FrameFileSize, sizeBytes, ct);
            }
            onProgress?.Invoke(0, size);
            var buffer = new byte[Protocol.ChunkSize];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                await WriteFrameAsync(FrameFileChunk, buffer.AsMemory(0, read), ct);
                total += read;
                onProgress?.Invoke(total, size);
            }
            await WriteFrameAsync(FrameFileEnd, ReadOnlyMemory<byte>.Empty, ct);
            AppLog.Write("SESSION", $"{name} file tx done bytes={total}");
        }
        catch (OperationCanceledException) when (started && ct.IsCancellationRequested)
        {
            // The receiver already opened a file for us: tell it to drop the partial data.
            AppLog.Write("SESSION", $"{name} file tx canceled by user, notifying peer");
            try
            {
                await WriteFrameAsync(FrameFileCancel, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLog.Error("SESSION", $"{name} cancel frame failed", ex);
            }
            throw;
        }
        finally
        {
            fileLock.Release();
        }
    }

    async Task WriteFrameAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            var header = new byte[5];
            header[0] = type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
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
        ReceivedFile? file = null;
        string fileName = "file";
        ChatMessage? incoming = null;
        long expected = -1;
        long received = 0;
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

                switch (header[0])
                {
                    case FramePing:
                        AppLog.Write("SESSION", $"{name} ping rx");
                        break;
                    case FrameText:
                        AppLog.Write("SESSION", $"{name} text rx bytes={length}");
                        MessageReceived?.Invoke(new ChatMessage { Text = Encoding.UTF8.GetString(payload) });
                        break;
                    case FrameFileStart:
                        await AbortFileAsync(file);
                        file = null;
                        incoming?.Fail();
                        fileName = Path.GetFileName(Encoding.UTF8.GetString(payload));
                        if (string.IsNullOrWhiteSpace(fileName)) fileName = "file";
                        file = await store.CreateAsync(fileName, ct);
                        fileName = file.Name;
                        expected = -1;
                        received = 0;
                        incoming = new ChatMessage { Text = fileName, IsFile = true, Location = file.Location, ShowProgress = true };
                        AppLog.Write("SESSION", $"{name} file rx start {fileName}");
                        MessageReceived?.Invoke(incoming);
                        break;
                    case FrameFileSize:
                        if (incoming != null && payload.Length == 8)
                        {
                            expected = BinaryPrimitives.ReadInt64LittleEndian(payload);
                            incoming.Report(received, expected);
                        }
                        break;
                    case FrameFileChunk:
                        if (file != null)
                        {
                            await file.Stream.WriteAsync(payload, ct);
                            received += payload.Length;
                            incoming?.Report(received, expected);
                        }
                        break;
                    case FrameFileEnd:
                        if (file != null)
                        {
                            await file.Stream.DisposeAsync();
                            await file.Complete();
                            file = null;
                            AppLog.Write("SESSION", $"{name} file rx done {fileName}");
                            incoming?.Complete();
                            incoming = null;
                        }
                        break;
                    case FrameFileCancel:
                        if (file != null || incoming != null)
                        {
                            AppLog.Write("SESSION", $"{name} file rx canceled by sender {fileName}");
                            await AbortFileAsync(file);
                            file = null;
                            incoming?.Fail("fileCanceledByPeer");
                            incoming = null;
                        }
                        break;
                    default:
                        AppLog.Write("SESSION", $"{name} unknown frame type={header[0]}");
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
            await AbortFileAsync(file);
            incoming?.Fail();
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

    public void Dispose()
    {
        AppLog.Write("SESSION", $"{name} Dispose called, caller: {AppLog.Caller()}");
        stream.Dispose();
    }
}
