namespace BtChat;

public sealed class DuplexStream : Stream
{
    readonly Stream input;
    readonly Stream output;
    readonly Action onClose;
    readonly string name;
    bool closed;

    public DuplexStream(Stream input, Stream output, Action onClose, string name)
    {
        this.input = input;
        this.output = output;
        this.onClose = onClose;
        this.name = name;
        AppLog.Write("STREAM", $"{name} created");
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => output.Flush();
    public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => output.WriteAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !closed)
        {
            closed = true;
            AppLog.Write("STREAM", $"{name} closing, caller: {AppLog.Caller(3)}");
            try { onClose(); } catch (Exception ex) { AppLog.Error("STREAM", $"{name} onClose failed", ex); }
            try { input.Dispose(); } catch { }
            try { output.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
