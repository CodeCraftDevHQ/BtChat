namespace BtChat;
public sealed record VideoFrame(byte[] Jpeg, byte Rotation);

public interface ICallVideo
{
    bool IsSupported { get; }
    int Quality { get; set; }
    bool Start(bool front, Action<VideoFrame> onFrame);
    void Stop();
}

public interface IVideoSurface
{
    void Show(VideoFrame frame);
    void Clear();
    void SetMirror(bool mirror);
}
public sealed class VideoFrameView : View
{
    public IVideoSurface? Backend { get; set; }
    public bool Crop { get; set; }

    public void Show(VideoFrame frame) => Backend?.Show(frame);
    public void Clear() => Backend?.Clear();
    public void SetMirror(bool mirror) => Backend?.SetMirror(mirror);
}

#if !ANDROID
public sealed class NoCallVideo : ICallVideo
{
    public bool IsSupported => false;
    public int Quality { get; set; } = 1;
    public bool Start(bool front, Action<VideoFrame> onFrame) => false;
    public void Stop() { }
}
#endif
