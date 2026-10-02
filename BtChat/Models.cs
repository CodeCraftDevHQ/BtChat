namespace BtChat;

public record BtDevice(string Name, string Id);

public static class Protocol
{
    public const string ServiceId = "8f3c2a10-5b7e-4d1a-9c64-2e7d0b1a4f33";
    public const int MaxFrame = 1 << 20;
    public const int ChunkSize = 32 * 1024;
    public const int PingIntervalMs = 5000;
    public const int PingTimeoutMs = 20000;
}

public class ChatMessage
{
    public string Text { get; init; } = "";
    public bool IsMine { get; init; }
    public bool IsFile { get; init; }
    public bool IsText => !IsFile;
    public string? FilePath { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    public string Display => IsFile ? "📎 " + Text : Text;
    public string TimeText => Time.ToString("HH:mm");
    public LayoutOptions Align => IsMine ? LayoutOptions.End : LayoutOptions.Start;
    public Color Bubble => IsMine ? Color.FromArgb("#DCF8C6") : Color.FromArgb("#ECECEC");
}

public interface IBluetoothTransport
{
    Task<bool> EnsurePermissionsAsync();
    Task<IReadOnlyList<BtDevice>> GetPairedDevicesAsync();
    Task<Stream> ConnectAsync(BtDevice device, CancellationToken ct);
    Task<Stream> AcceptAsync(CancellationToken ct);
}
