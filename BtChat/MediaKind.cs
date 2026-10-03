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

public sealed class MediaPlayerView : View
{
    public static readonly BindableProperty SourceProperty =
        BindableProperty.Create(nameof(Source), typeof(string), typeof(MediaPlayerView), null);

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }
}
