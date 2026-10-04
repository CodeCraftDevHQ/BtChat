namespace BtChat;

// One camera picture: a JPEG plus how many quarter turns (clockwise) make it upright.
public sealed record VideoFrame(byte[] Jpeg, byte Rotation);

public interface ICallVideo
{
    bool IsSupported { get; }
    // Opens the camera and calls onFrame for every captured picture (about 12 per second).
    bool Start(bool front, Action<VideoFrame> onFrame);
    void Stop();
}

public interface IVideoSurface
{
    void Show(VideoFrame frame);
    void Clear();
    void SetMirror(bool mirror);
}

// A picture box that shows JPEG frames. The platform handler does the drawing.
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
    public bool Start(bool front, Action<VideoFrame> onFrame) => false;
    public void Stop() { }
}
#endif
