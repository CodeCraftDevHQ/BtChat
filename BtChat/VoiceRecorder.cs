namespace BtChat;

public interface IVoiceRecorder
{
    bool IsSupported { get; }
    bool Start(string path);
    // Stops recording and returns its length. When keep is false the file is deleted.
    TimeSpan Stop(bool keep);
}

#if !ANDROID
public sealed class NoVoiceRecorder : IVoiceRecorder
{
    public bool IsSupported => false;

    public bool Start(string path) => false;

    public TimeSpan Stop(bool keep) => TimeSpan.Zero;
}
#endif
