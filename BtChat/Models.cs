using CommunityToolkit.Mvvm.ComponentModel;

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

public class ChatMessage : ObservableObject
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

    double progress;
    bool showProgress;
    bool failed;
    string statusText = "";
    long lastKey = -1;

    public double Progress { get => progress; private set => SetProperty(ref progress, value); }
    public bool ShowProgress { get => showProgress; set => SetProperty(ref showProgress, value); }
    public bool Failed { get => failed; private set => SetProperty(ref failed, value); }
    public bool HasStatus => statusText.Length > 0;
    public string StatusText
    {
        get => statusText;
        private set
        {
            if (SetProperty(ref statusText, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    // total < 0 means the size is unknown: only the transferred bytes are shown.
    public void Report(long done, long total)
    {
        var key = total > 0 ? done * 100 / total : done / (256 * 1024);
        if (key == lastKey) return;
        lastKey = key;
        var p = total > 0 ? Math.Min(1.0, (double)done / total) : 0;
        var text = total > 0
            ? $"{(int)(p * 100)}%  ({FormatSize(done)} / {FormatSize(total)})"
            : FormatSize(done);
        OnUi(() =>
        {
            Progress = p;
            StatusText = text;
            ShowProgress = true;
        });
    }

    public void Complete() => OnUi(() =>
    {
        Progress = 1;
        ShowProgress = false;
        StatusText = "";
    });

    public void Fail() => OnUi(() =>
    {
        Failed = true;
        ShowProgress = false;
        StatusText = "⚠ " + Loc.Instance["fileFailed"];
    });

    static void OnUi(Action action)
    {
        if (MainThread.IsMainThread) action();
        else MainThread.BeginInvokeOnMainThread(action);
    }

    static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:F1} {units[i]}";
    }
}

public interface IBluetoothTransport
{
    Task<bool> EnsurePermissionsAsync();
    Task<IReadOnlyList<BtDevice>> GetPairedDevicesAsync();
    Task<Stream> ConnectAsync(BtDevice device, CancellationToken ct);
    Task<Stream> AcceptAsync(CancellationToken ct);
}
