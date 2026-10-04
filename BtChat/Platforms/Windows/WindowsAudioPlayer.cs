using Windows.Media.Core;
using Windows.Media.Playback;

namespace BtChat;

public sealed class WindowsAudioPlayer : IAudioPlayer
{
    MediaPlayer? player;
    bool prepared;

    public bool IsSupported => true;

    public event Action? Completed;

    public long Position => player != null && prepared ? (long)player.PlaybackSession.Position.TotalMilliseconds : 0;

    public long Duration => player != null && prepared ? (long)player.PlaybackSession.NaturalDuration.TotalMilliseconds : 0;

    public bool IsPlaying => player != null && prepared && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public async Task PlayAsync(string source, long startMs, float speed)
    {
        Stop();
        var current = new MediaPlayer { AutoPlay = false };
        player = current;
        var ready = new TaskCompletionSource();
        current.MediaOpened += (_, _) => ready.TrySetResult();
        current.MediaFailed += (_, e) => ready.TrySetException(new IOException($"audio error {e.Error}"));
        current.MediaEnded += (_, _) =>
        {
            if (ReferenceEquals(player, current)) Completed?.Invoke();
        };
        try
        {
            current.Source = MediaSource.CreateFromUri(new Uri(source));
            await ready.Task;
            if (!ReferenceEquals(player, current)) return;
            prepared = true;
            if (startMs > 0) current.PlaybackSession.Position = TimeSpan.FromMilliseconds(startMs);
            current.PlaybackSession.PlaybackRate = speed;
            current.Play();
        }
        catch
        {
            if (ReferenceEquals(player, current)) Stop();
            else current.Dispose();
            throw;
        }
    }

    public void Pause()
    {
        if (player != null && prepared) player.Pause();
    }

    public void Resume(float speed)
    {
        if (player == null || !prepared) return;
        player.PlaybackSession.PlaybackRate = speed;
        player.Play();
    }

    public void Seek(long ms)
    {
        if (player != null && prepared) player.PlaybackSession.Position = TimeSpan.FromMilliseconds(Math.Max(0, ms));
    }

    public void SetSpeed(float speed)
    {
        if (player != null && prepared) player.PlaybackSession.PlaybackRate = speed;
    }

    public void Stop()
    {
        var current = player;
        player = null;
        prepared = false;
        current?.Dispose();
    }

    public async Task<long> GetDurationAsync(string source)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(source);
            var props = await file.Properties.GetMusicPropertiesAsync();
            return (long)props.Duration.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            AppLog.Error("AUDIO", "reading the duration failed", ex);
            return 0;
        }
    }
}
