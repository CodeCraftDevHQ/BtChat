using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinImage = Microsoft.UI.Xaml.Controls.Image;
using WinBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;

namespace BtChat;

// Shows the JPEG frames of a video call (the other person, or the small preview of yourself).
public sealed class VideoFrameViewHandler : ViewHandler<VideoFrameView, WinGrid>, IVideoSurface
{
    public static readonly IPropertyMapper<VideoFrameView, VideoFrameViewHandler> PropertyMapper =
        new PropertyMapper<VideoFrameView, VideoFrameViewHandler>(ViewHandler.ViewMapper);

    WinImage? image;
    SoftwareBitmapSource? bitmapSource;
    VideoFrame? pending;
    int busy;

    public VideoFrameViewHandler() : base(PropertyMapper)
    {
    }

    protected override WinGrid CreatePlatformView()
    {
        image = new WinImage
        {
            Stretch = VirtualView.Crop ? Microsoft.UI.Xaml.Media.Stretch.UniformToFill : Microsoft.UI.Xaml.Media.Stretch.Uniform,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
        };
        var grid = new WinGrid { Background = new WinBrush(Microsoft.UI.Colors.Black) };
        grid.Children.Add(image);
        return grid;
    }

    protected override void ConnectHandler(WinGrid platformView)
    {
        base.ConnectHandler(platformView);
        VirtualView.Backend = this;
    }

    protected override void DisconnectHandler(WinGrid platformView)
    {
        VirtualView.Backend = null;
        base.DisconnectHandler(platformView);
    }

    public void SetMirror(bool mirror) =>
        PlatformView?.DispatcherQueue.TryEnqueue(() =>
        {
            if (image != null) image.RenderTransform = mirror ? new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = -1 } : null;
        });

    public void Clear()
    {
        Interlocked.Exchange(ref pending, null);
        PlatformView?.DispatcherQueue.TryEnqueue(() =>
        {
            if (image != null) image.Source = null;
            bitmapSource = null;
        });
    }

    // Only the newest frame matters: while one is being drawn, newer frames replace the waiting one.
    public void Show(VideoFrame frame)
    {
        Interlocked.Exchange(ref pending, frame);
        if (Interlocked.CompareExchange(ref busy, 1, 0) == 0) _ = Task.Run(DrainAsync);
    }

    async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                var frame = Interlocked.Exchange(ref pending, null);
                if (frame == null) break;
                await RenderAsync(frame);
            }
        }
        finally
        {
            Volatile.Write(ref busy, 0);
            if (pending != null && Interlocked.CompareExchange(ref busy, 1, 0) == 0) _ = Task.Run(DrainAsync);
        }
    }

    async Task RenderAsync(VideoFrame frame)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(frame.Jpeg.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            // Rotation = number of clockwise quarter turns, as on Android.
            var transform = new BitmapTransform
            {
                Rotation = (frame.Rotation % 4) switch
                {
                    1 => BitmapRotation.Clockwise90Degrees,
                    2 => BitmapRotation.Clockwise180Degrees,
                    3 => BitmapRotation.Clockwise270Degrees,
                    _ => BitmapRotation.None
                }
            };
            var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var queue = PlatformView?.DispatcherQueue;
            if (queue == null)
            {
                bitmap.Dispose();
                return;
            }
            var done = new TaskCompletionSource();
            queue.TryEnqueue(async () =>
            {
                try
                {
                    // The bitmap source has to be made and filled on the UI thread.
                    if (bitmapSource == null)
                    {
                        bitmapSource = new SoftwareBitmapSource();
                        if (image != null) image.Source = bitmapSource;
                    }
                    await bitmapSource.SetBitmapAsync(bitmap);
                }
                catch (Exception ex)
                {
                    AppLog.Error("CALL", "showing a video frame failed", ex);
                }
                finally
                {
                    bitmap.Dispose();
                    done.TrySetResult();
                }
            });
            await done.Task;
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "drawing a video frame failed", ex);
        }
    }
}
