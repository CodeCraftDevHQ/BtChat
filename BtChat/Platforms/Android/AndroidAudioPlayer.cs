using Android.Media;
using AndroidUri = Android.Net.Uri;

namespace BtChat;

public sealed class AndroidAudioPlayer : IAudioPlayer
{
    MediaPlayer? player;
    bool prepared;

    public bool IsSupported => true;

    public event Action? Completed;

    public long Position => player != null && prepared ? player.CurrentPosition : 0;

    public long Duration => player != null && prepared ? Math.Max(0, player.Duration) : 0;

    public bool IsPlaying => player != null && prepared && player.IsPlaying;

    public async Task PlayAsync(string source, long startMs, float speed)
    {
        Stop();
        var current = new MediaPlayer();
        player = current;
        var ready = new TaskCompletionSource();
        current.Prepared += (_, _) => ready.TrySetResult();
        current.Error += (_, e) =>
        {
            e.Handled = true;
            ready.TrySetException(new IOException($"audio error {e.What}/{e.Extra}"));
        };
        current.Completion += (_, _) =>
        {
            if (ReferenceEquals(player, current)) Completed?.Invoke();
        };
        try
        {
            if (source.StartsWith("content://", StringComparison.Ordinal))
                current.SetDataSource(Android.App.Application.Context, AndroidUri.Parse(source)!);
            else
                current.SetDataSource(source);
            current.PrepareAsync();
            await ready.Task;
            if (!ReferenceEquals(player, current)) return;
            prepared = true;
            if (startMs > 0) current.SeekTo((int)startMs);
            current.Start();
            ApplySpeed(speed);
        }
        catch
        {
            if (ReferenceEquals(player, current)) Stop();
            else Release(current);
            throw;
        }
    }

    public void Pause()
    {
        if (player != null && prepared && player.IsPlaying) player.Pause();
    }

    public void Resume(float speed)
    {
        if (player == null || !prepared) return;
        player.Start();
        ApplySpeed(speed);
    }

    public void Seek(long ms)
    {
        if (player != null && prepared) player.SeekTo((int)Math.Clamp(ms, 0, int.MaxValue));
    }

    public void SetSpeed(float speed)
    {
        if (IsPlaying) ApplySpeed(speed);
    }

    void ApplySpeed(float speed)
    {
        try
        {
            var parameters = player!.PlaybackParams;
            parameters.SetSpeed(speed);
            player.PlaybackParams = parameters;
        }
        catch (Exception ex)
        {
            AppLog.Error("AUDIO", $"set speed {speed} failed", ex);
        }
    }

    public void Stop()
    {
        var current = player;
        player = null;
        prepared = false;
        if (current != null) Release(current);
    }

    static void Release(MediaPlayer current)
    {
        try
        {
            current.Release();
        }
        catch
        {
        }
        current.Dispose();
    }

    public Task<long> GetDurationAsync(string source) => Task.Run(() =>
    {
        var retriever = new MediaMetadataRetriever();
        try
        {
            if (source.StartsWith("content://", StringComparison.Ordinal))
                retriever.SetDataSource(Android.App.Application.Context, AndroidUri.Parse(source)!);
            else
                retriever.SetDataSource(source);
            return long.TryParse(retriever.ExtractMetadata(MetadataKey.Duration), out var ms) ? ms : 0L;
        }
        catch (Exception ex)
        {
            AppLog.Error("AUDIO", "reading the duration failed", ex);
            return 0L;
        }
        finally
        {
            try { retriever.Release(); } catch { }
        }
    });
}
