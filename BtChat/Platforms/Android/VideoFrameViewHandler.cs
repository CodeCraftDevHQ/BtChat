using Android.Graphics;
using Android.Widget;
using Microsoft.Maui.Handlers;

namespace BtChat;

public sealed class VideoFrameViewHandler : ViewHandler<VideoFrameView, ImageView>, IVideoSurface
{
    public static readonly IPropertyMapper<VideoFrameView, VideoFrameViewHandler> PropertyMapper =
        new PropertyMapper<VideoFrameView, VideoFrameViewHandler>(ViewHandler.ViewMapper);

    VideoFrame? pending;
    int busy;
    volatile bool mirror;

    public VideoFrameViewHandler() : base(PropertyMapper)
    {
    }

    protected override ImageView CreatePlatformView()
    {
        var view = new ImageView(Context);
        view.SetBackgroundColor(Android.Graphics.Color.Black);
        view.SetScaleType(VirtualView.Crop ? ImageView.ScaleType.CenterCrop : ImageView.ScaleType.FitCenter);
        return view;
    }

    protected override void ConnectHandler(ImageView platformView)
    {
        base.ConnectHandler(platformView);
        VirtualView.Backend = this;
    }

    protected override void DisconnectHandler(ImageView platformView)
    {
        VirtualView.Backend = null;
        base.DisconnectHandler(platformView);
    }

    public void SetMirror(bool value) => mirror = value;

    public void Clear()
    {
        Interlocked.Exchange(ref pending, null);
        PlatformView?.Post(() => PlatformView.SetImageBitmap(null));
    }

    public void Show(VideoFrame frame)
    {
        Interlocked.Exchange(ref pending, frame);
        if (Interlocked.CompareExchange(ref busy, 1, 0) == 0) _ = Task.Run(Drain);
    }

    void Drain()
    {
        try
        {
            while (true)
            {
                var frame = Interlocked.Exchange(ref pending, null);
                if (frame == null) break;
                Render(frame);
            }
        }
        finally
        {
            Volatile.Write(ref busy, 0);
            if (pending != null && Interlocked.CompareExchange(ref busy, 1, 0) == 0) _ = Task.Run(Drain);
        }
    }

    void Render(VideoFrame frame)
    {
        try
        {
            var bitmap = BitmapFactory.DecodeByteArray(frame.Jpeg, 0, frame.Jpeg.Length);
            if (bitmap == null) return;
            var turns = frame.Rotation % 4;
            if (turns != 0 || mirror)
            {
                var matrix = new Matrix();
                if (turns != 0) matrix.PostRotate(turns * 90f);
                if (mirror) matrix.PostScale(-1f, 1f);
                var changed = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true);
                if (!ReferenceEquals(changed, bitmap)) bitmap.Recycle();
                bitmap = changed;
            }
            var shown = bitmap;
            PlatformView?.Post(() => PlatformView.SetImageBitmap(shown));
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "drawing a video frame failed", ex);
        }
    }
}
