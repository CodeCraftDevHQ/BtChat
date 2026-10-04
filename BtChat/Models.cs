using CommunityToolkit.Mvvm.ComponentModel;

namespace BtChat;

public record BtDevice(string Name, string Id);

public static class Protocol
{
    public const string ServiceId = "8f3c2a10-5b7e-4d1a-9c64-2e7d0b1a4f33";
    public const int MaxFrame = 1 << 20;
    public const int TcpChunkSize = 128 * 1024;
    public const int BluetoothChunkSize = 32 * 1024;
    public const int AcceptTimeoutSeconds = 30;
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
    public Guid MessageId { get; set; }
    bool isEdited;
    public bool IsEdited
    {
        get => isEdited;
        set => SetProperty(ref isEdited, value);
    }
    public string SenderName { get; init; } = "";
    public bool HasSender => SenderName.Length > 0;
    public bool IsFile { get; init; }
    public bool IsText => !IsFile;
    public bool IsReceivedFile => IsFile && !IsMine;
    public bool HasMenu => IsText || (IsFile && !ShowProgress);
    public bool CanCancel => IsMine && IsFile && ShowProgress;
    // Identifies one file transfer across connections (the same key is used when it is offered again).
    public Guid TransferKey { get; set; }
    // A failed file can be tried again: the sender sends it again, the receiver asks the sender to do so.
    public bool CanRetry => IsFile && failed && !showProgress && (IsMine ? location != null : TransferKey != Guid.Empty);
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

    public static bool InlineAudio { get; set; }
    public static Func<string, Task<long>>? DurationProbe { get; set; }
    long audioPosition;
    long audioDuration;
    bool audioPlaying;
    bool durationProbed;
    public bool AudioSeeking { get; set; }
    public bool ShowAudioPlayer => InlineAudio && Ready && IsAudio;
    public bool IsVoice => IsFile && text.StartsWith("voice_", StringComparison.OrdinalIgnoreCase);
    public bool ShowName => !(ShowAudioPlayer && IsVoice);
    public string AudioGlyph => audioPlaying ? "⏸" : "▶";
    public double AudioProgress => audioDuration > 0 ? Math.Clamp((double)audioPosition / audioDuration, 0, 1) : 0;

    public long AudioPosition
    {
        get => audioPosition;
        set
        {
            if (!SetProperty(ref audioPosition, value)) return;
            OnPropertyChanged(nameof(AudioProgress));
            OnPropertyChanged(nameof(AudioTime));
        }
    }

    public long AudioDuration
    {
        get => audioDuration;
        set
        {
            if (!SetProperty(ref audioDuration, value)) return;
            OnPropertyChanged(nameof(AudioProgress));
            OnPropertyChanged(nameof(AudioTime));
        }
    }

    public bool AudioPlaying
    {
        get => audioPlaying;
        set
        {
            if (SetProperty(ref audioPlaying, value)) OnPropertyChanged(nameof(AudioGlyph));
        }
    }

    public string AudioTime
    {
        get
        {
            ProbeDuration();
            if (audioDuration <= 0) return FormatClock(audioPosition);
            return audioPlaying || audioPosition > 0
                ? FormatClock(audioPosition) + " / " + FormatClock(audioDuration)
                : FormatClock(audioDuration);
        }
    }

    static string FormatClock(long ms)
    {
        var total = (int)(Math.Max(0, ms) / 1000);
        return $"{total / 60}:{total % 60:00}";
    }

    void ProbeDuration()
    {
        if (durationProbed || audioDuration > 0 || !ShowAudioPlayer || location == null || DurationProbe == null) return;
        durationProbed = true;
        var target = location;
        var probe = DurationProbe;
        _ = Task.Run(async () =>
        {
            var duration = await probe(target);
            if (duration > 0) MainThread.BeginInvokeOnMainThread(() => AudioDuration = duration);
        });
    }

    string? thumbFor;
    ImageSource? thumb;
    string? videoThumbFor;
    ImageSource? videoThumb;
    byte[]? videoThumbBytes;

    public static Func<string, Task<byte[]?>>? VideoThumbOpener { get; set; }

    public ImageSource? VideoThumb
    {
        get
        {
            if (!ShowVideoBox) return null;
            if (videoThumb != null && videoThumbFor == location) return videoThumb;
            var target = location!;
            videoThumbFor = target;
            videoThumbBytes = null;
            videoThumb = new StreamImageSource
            {
                Stream = async _ =>
                {
                    if (videoThumbBytes == null && VideoThumbOpener != null)
                    {
                        try
                        {
                            videoThumbBytes = await VideoThumbOpener(target);
                        }
                        catch
                        {
                            videoThumbBytes = null;
                        }
                    }
                    return new MemoryStream(videoThumbBytes ?? Array.Empty<byte>());
                }
            };
            return videoThumb;
        }
    }

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
        OnPropertyChanged(nameof(ShowAudioPlayer));
        OnPropertyChanged(nameof(ShowName));
        OnPropertyChanged(nameof(AudioTime));
        OnPropertyChanged(nameof(Thumb));
        OnPropertyChanged(nameof(VideoThumb));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(HasInfo));
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
    public event Action? Started;

    double progress;
    string? failKey;
    bool showProgress;
    bool failed;
    string statusText = "";
    long lastKey = -1;
    long lastDone;
    long partialBytes;
    long startTicks;
    long sizeBytes;
    double durationSeconds;

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
                OnPropertyChanged(nameof(CanRetry));
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
                OnPropertyChanged(nameof(CanRetry));
                NotifyMedia();
            }
        }
    }
    public string? FailKey => failKey;
    public long SizeBytes => sizeBytes;
    public long PartialBytes => partialBytes;
    public long LastDone => lastDone;
    public double DurationSeconds => durationSeconds;

    public string InfoText
    {
        get
        {
            if (!IsFile || showProgress) return "";
            var parts = new List<string>();
            if (sizeBytes > 0) parts.Add(FormatSize(sizeBytes));
            if (durationSeconds > 0 && !failed) parts.Add(FormatDuration(durationSeconds));
            return string.Join("  •  ", parts);
        }
    }

    public bool HasInfo => InfoText.Length > 0;

    static string FormatDuration(double seconds)
    {
        var loc = Loc.Instance;
        if (seconds < 60)
        {
            var value = seconds < 10
                ? seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                : ((int)Math.Round(seconds)).ToString();
            return value + " " + loc["secShort"];
        }
        var minutes = (int)(seconds / 60);
        var rest = (int)Math.Round(seconds - minutes * 60);
        if (rest == 60)
        {
            minutes++;
            rest = 0;
        }
        return $"{minutes}:{rest:D2} {loc["minShort"]}";
    }
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
        lastDone = done;
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
    // startAt > 0: a partly received file is continued from that byte.
    public void Begin(string name, string fileLocation, long expected, long startAt = 0)
    {
        OnUi(() =>
        {
            Text = name;
            Location = fileLocation;
            Started?.Invoke();
        });
        lastKey = -1;
        MarkStart(expected);
        Report(startAt, expected);
    }

    // Receiver: remember the announced size, so it is known even if the transfer never starts.
    public void SetOffered(long size)
    {
        if (size > 0) sizeBytes = size;
    }

    // A failed transfer is going to be tried again: back to the waiting state (progress kept for the status text).
    public void Revive(string queuedKey = "queued") => OnUi(() =>
    {
        failKey = null;
        lastDone = partialBytes;
        Failed = false;
        SetQueued(queuedKey);
    });

    // What was received so far is gone (cancelled, deleted or outdated): the next try starts from zero.
    public void DropPartial() => OnUi(() =>
    {
        partialBytes = 0;
        lastDone = 0;
        if (!IsMine) Location = null;
    });

    // A short remark in the status line of a failed file (for example "connect first").
    public void ShowNote(string key) => OnUi(() => StatusText = "⚠ " + Loc.Instance[key]);

    public void MarkStart(long size)
    {
        startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (size > 0) sizeBytes = size;
    }

    public void Complete()
    {
        if (startTicks != 0) durationSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(startTicks).TotalSeconds;
        if (sizeBytes <= 0) sizeBytes = lastDone;
        CompleteOnUi();
    }

    void CompleteOnUi() => OnUi(() =>
    {
        Progress = 1;
        ShowProgress = false;
        StatusText = "";
        Finished?.Invoke();
    });

    // keepPartial: the bytes already transferred stay valid (the receiver keeps its partial file).
    public void Fail(string key = "fileFailed", bool keepPartial = true) => OnUi(() =>
    {
        failKey = key;
        partialBytes = keepPartial ? lastDone : 0;
        if (!keepPartial && !IsMine) Location = null;
        Failed = true;
        ShowProgress = false;
        StatusText = FailText();
        Finished?.Invoke();
    });

    string FailText()
    {
        var text = "⚠ " + Loc.Instance[failKey ?? "fileFailed"];
        if (IsFile && partialBytes > 0 && sizeBytes > partialBytes)
            text += $" ({partialBytes * 100 / sizeBytes}%)";
        return text;
    }

    public static ChatMessage Restore(StoredMessage s)
    {
        var m = new ChatMessage
        {
            Text = s.Text,
            IsMine = s.IsMine,
            IsFile = s.IsFile,
            Location = s.Location,
            Time = s.Time,
            SenderName = s.SenderName,
            sizeBytes = s.SizeBytes,
            durationSeconds = s.DurationSeconds,
            partialBytes = s.PartialBytes,
            TransferKey = Guid.TryParse(s.TransferKey, out var key) ? key : Guid.Empty,
            MessageId = Guid.TryParse(s.MessageId, out var messageId) ? messageId : Guid.Empty,
            IsEdited = s.IsEdited
        };
        if (s.IsFile && s.FailKey != null)
        {
            m.failKey = s.FailKey;
            m.failed = true;
            m.statusText = m.FailText();
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
    public long ExistingLength { get; init; }
}

public interface IReceivedFileStore
{
    Task EnsureReadyAsync();
    Task<Stream> OpenReadAsync(string location);
    Task<byte[]?> GetVideoThumbnailAsync(string location);
    Task<ReceivedFile> CreateAsync(string folder, string fileName, CancellationToken ct);
    // Reopens a partly received file for appending (ExistingLength = bytes already there). Null if it is gone.
    Task<ReceivedFile?> OpenForResumeAsync(string location, CancellationToken ct);
    Task OpenAsync(string location, string name);
    Task ShowInFolderAsync(string location);
    Task ShareAsync(string location, string name);
    Task DeleteAsync(string location);
}
