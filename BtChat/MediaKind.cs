namespace BtChat;

public enum MediaKind
{
    None,
    Image,
    Video,
    Audio
}

public static class MediaKinds
{
    static readonly HashSet<string> images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
    };

    static readonly HashSet<string> videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".3gp", ".webm", ".mkv", ".avi"
    };

    static readonly HashSet<string> audios = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".ogg", ".oga", ".opus", ".flac", ".amr"
    };

    public static MediaKind FromName(string? name)
    {
        var ext = Path.GetExtension(name ?? "");
        if (ext.Length == 0) return MediaKind.None;
        if (images.Contains(ext)) return MediaKind.Image;
        if (videos.Contains(ext)) return MediaKind.Video;
        if (audios.Contains(ext)) return MediaKind.Audio;
        return MediaKind.None;
    }
}

public enum PlayerTouch
{
    Down,
    Move,
    Up,
    Cancel
}

// What the platform player (Android VideoView / Windows MediaPlayer) offers to the shared player screen.
public interface IPlayerBackend
{
    void Play();
    void Pause();
    void SeekTo(long ms);
    // Applied while playing (some Android versions start a paused player when the speed is set).
    void SetSpeed(float speed);
    long Position { get; }
    long Duration { get; }
    bool IsPlaying { get; }
}

public sealed class MediaPlayerView : View
{
    public static readonly BindableProperty SourceProperty =
        BindableProperty.Create(nameof(Source), typeof(string), typeof(MediaPlayerView), null);

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IPlayerBackend? Backend { get; set; }

    public event Action? Prepared;
    public event Action? Ended;
    public event Action<string>? Failed;
    // Raw touches on the picture, in device-independent units relative to the player.
    public event Action<PlayerTouch, double, double>? Touched;

    public void Play() => Backend?.Play();
    public void Pause() => Backend?.Pause();
    public void SeekTo(long ms) => Backend?.SeekTo(ms);
    public void SetSpeed(float speed) => Backend?.SetSpeed(speed);
    public long GetPosition() => Backend?.Position ?? 0;
    public long GetDuration() => Backend?.Duration ?? 0;
    public bool GetIsPlaying() => Backend?.IsPlaying ?? false;

    public void RaisePrepared() => Prepared?.Invoke();
    public void RaiseEnded() => Ended?.Invoke();
    public void RaiseFailed(string reason) => Failed?.Invoke(reason);
    public void RaiseTouch(PlayerTouch phase, double x, double y) => Touched?.Invoke(phase, x, y);
}
