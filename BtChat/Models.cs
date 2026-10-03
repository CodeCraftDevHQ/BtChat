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
    string text = "";
    string? location;
    public string Text
    {
        get => text;
        set
        {
            if (SetProperty(ref text, value)) NotifyMedia();
        }
    }
    public bool IsMine { get; init; }
    public string SenderName { get; init; } = "";
    public bool HasSender => SenderName.Length > 0;
    public bool IsFile { get; init; }
    public bool IsText => !IsFile;
    public bool IsReceivedFile => IsFile && !IsMine;
    public bool HasMenu => IsText || (IsFile && !ShowProgress);
    public bool CanCancel => IsMine && IsFile && ShowProgress;
    public string? Location
    {
        get => location;
        set
        {
            if (SetProperty(ref location, value)) NotifyMedia();
        }
    }

    public static Func<string, Task<Stream>>? Opener { get; set; }

    public MediaKind Kind => IsFile ? MediaKinds.FromName(text) : MediaKind.None;
    public bool IsMedia => Kind != MediaKind.None;
    public bool IsAudio => Kind == MediaKind.Audio;
    public bool IsVideo => Kind == MediaKind.Video;
    public bool IsImage => Kind == MediaKind.Image;
    public bool Ready => IsFile && location != null && !showProgress && !failed;
    public bool CanPreview => Ready && IsMedia;
    public bool ShowImage => Ready && IsImage;
    public bool ShowVideoBox => Ready && IsVideo;

    string? thumbFor;
    ImageSource? thumb;

    public ImageSource? Thumb
    {
        get
        {
            if (!ShowImage) return null;
            if (thumb != null && thumbFor == location) return thumb;
            var target = location!;
            thumbFor = target;
            thumb = new StreamImageSource
            {
                Stream = async _ =>
                {
                    try
                    {
                        return await Opener!(target);
                    }
                    catch
                    {
                        return new MemoryStream();
                    }
                }
            };
            return thumb;
        }
    }

    void NotifyMedia()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(ShowImage));
        OnPropertyChanged(nameof(ShowVideoBox));
        OnPropertyChanged(nameof(Thumb));
    }
    public DateTime Time { get; init; } = DateTime.Now;
    public string Display => !IsFile ? Text : Kind switch
    {
        MediaKind.Image => "🖼 " + Text,
        MediaKind.Video => "🎬 " + Text,
        MediaKind.Audio => "🎵 " + Text,
        _ => "📎 " + Text
    };
    public string TimeText => Time.ToString("HH:mm");
    public LayoutOptions Align => IsMine ? LayoutOptions.End : LayoutOptions.Start;
    public Color Bubble => IsMine ? Color.FromArgb("#DCF8C6") : Color.FromArgb("#ECECEC");

    public CancellationTokenSource? Cts { get; set; }
    public event Action? Finished;

    double progress;
    string? failKey;
    bool showProgress;
    bool failed;
    string statusText = "";
    long lastKey = -1;

    public double Progress { get => progress; private set => SetProperty(ref progress, value); }
    public bool ShowProgress
    {
        get => showProgress;
        set
        {
            if (SetProperty(ref showProgress, value))
            {
                OnPropertyChanged(nameof(HasMenu));
                OnPropertyChanged(nameof(CanCancel));
                NotifyMedia();
            }
        }
    }
    public bool Failed
    {
        get => failed;
        private set
        {
            if (SetProperty(ref failed, value))
            {
                OnPropertyChanged(nameof(HasMenu));
                NotifyMedia();
            }
        }
    }
    public string? FailKey => failKey;
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

    // Waiting for its turn (sender) or for the sender to start it (receiver).
    public void SetQueued(string key = "queued") => OnUi(() =>
    {
        lastKey = -1;
        Progress = 0;
        StatusText = Loc.Instance[key];
        ShowProgress = true;
    });

    // Receiver: the real file now exists, so the final name and location are known.
    public void Begin(string name, string fileLocation, long expected)
    {
        OnUi(() =>
        {
            Text = name;
            Location = fileLocation;
        });
        lastKey = -1;
        Report(0, expected);
    }

    public void Complete() => OnUi(() =>
    {
        Progress = 1;
        ShowProgress = false;
        StatusText = "";
        Finished?.Invoke();
    });

    public void Fail(string key = "fileFailed") => OnUi(() =>
    {
        failKey = key;
        Failed = true;
        ShowProgress = false;
        StatusText = "⚠ " + Loc.Instance[key];
        Finished?.Invoke();
    });

    public static ChatMessage Restore(StoredMessage s)
    {
        var m = new ChatMessage
        {
            Text = s.Text,
            IsMine = s.IsMine,
            IsFile = s.IsFile,
            Location = s.Location,
            Time = s.Time,
            SenderName = s.SenderName
        };
        if (s.IsFile && s.FailKey != null)
        {
            m.failKey = s.FailKey;
            m.failed = true;
            m.statusText = "⚠ " + Loc.Instance[s.FailKey];
        }
        return m;
    }

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

public enum BtState
{
    Ready,
    NoPermission,
    Off,
    Unavailable
}

public interface IBluetoothTransport
{
    Task<BtState> GetStateAsync();
    Task<bool> EnableAsync();
    Task<IReadOnlyList<BtDevice>> GetPairedDevicesAsync();
    Task<Stream> ConnectAsync(BtDevice device, CancellationToken ct);
    Task<Stream> AcceptAsync(CancellationToken ct);
}

public sealed class ReceivedFile
{
    public required Stream Stream { get; init; }
    public required string Name { get; init; }
    public required string Location { get; init; }
    public required Func<Task> Complete { get; init; }
    public required Func<Task> Abort { get; init; }
}

public interface IReceivedFileStore
{
    Task EnsureReadyAsync();
    Task<Stream> OpenReadAsync(string location);
    Task<ReceivedFile> CreateAsync(string folder, string fileName, CancellationToken ct);
    Task OpenAsync(string location, string name);
    Task ShowInFolderAsync(string location);
    Task ShareAsync(string location, string name);
    Task DeleteAsync(string location);
}
