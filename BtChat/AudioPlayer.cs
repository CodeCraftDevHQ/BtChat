namespace BtChat;

public interface IAudioPlayer
{
    bool IsSupported { get; }
    long Position { get; }
    long Duration { get; }
    bool IsPlaying { get; }
    event Action? Completed;
    Task PlayAsync(string source, long startMs, float speed);
    void Pause();
    void Resume(float speed);
    void Seek(long ms);
    void SetSpeed(float speed);
    void Stop();
    Task<long> GetDurationAsync(string source);
}

#if !ANDROID && !WINDOWS
public sealed class NoAudioPlayer : IAudioPlayer
{
    public bool IsSupported => false;
    public long Position => 0;
    public long Duration => 0;
    public bool IsPlaying => false;
    public event Action? Completed
    {
        add { }
        remove { }
    }
    public Task PlayAsync(string source, long startMs, float speed) => Task.CompletedTask;
    public void Pause() { }
    public void Resume(float speed) { }
    public void Seek(long ms) { }
    public void SetSpeed(float speed) { }
    public void Stop() { }
    public Task<long> GetDurationAsync(string source) => Task.FromResult(0L);
}
#endif
