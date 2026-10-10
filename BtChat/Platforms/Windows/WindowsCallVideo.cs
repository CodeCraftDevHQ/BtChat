using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using WinMediaCapture = Windows.Media.Capture.MediaCapture;

namespace BtChat;

// Video for calls on Windows: the webcam through MediaCapture, every frame sent as a JPEG (like on Android).
public sealed class WindowsCallVideo : ICallVideo
{
    static readonly (int Pixels, int GapMs, int Jpeg)[] Profiles =
    {
        (320 * 240, 125, 40),
        (480 * 360, 80, 50),
        (640 * 480, 66, 60),
        (1280 * 720, 100, 70)
    };

    public int Quality { get; set; } = 1;

    int Profile => Math.Clamp(Quality, 0, Profiles.Length - 1);

    WinMediaCapture? capture;
    MediaFrameReader? reader;
    Action<VideoFrame>? onFrame;
    long lastFrameTicks;
    int busy;
    volatile bool running;
    int generation;

    public bool IsSupported => true;

    public bool Start(bool front, Action<VideoFrame> onFrame)
    {
        Stop();
        try
        {
            // Without a webcam there is nothing to start (checked off the UI thread, so it can not block it).
            var found = Task.Run(async () => (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).Count > 0)
                .GetAwaiter().GetResult();
            if (!found)
            {
                AppLog.Write("CALL", "no webcam found");
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "looking for a webcam failed", ex);
            return false;
        }
        this.onFrame = onFrame;
        running = true;
        var mine = Interlocked.Increment(ref generation);
        _ = Task.Run(() => OpenAsync(front, mine));
        return true;
    }

    async Task OpenAsync(bool front, int mine)
    {
        WinMediaCapture? opened = null;
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            var wanted = front ? Windows.Devices.Enumeration.Panel.Front : Windows.Devices.Enumeration.Panel.Back;
            var device = devices.FirstOrDefault(d => d.EnclosureLocation?.Panel == wanted) ?? devices.FirstOrDefault();
            if (device == null) return;

            opened = new WinMediaCapture();
            await opened.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl
            });

            var source = opened.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color
                    && s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                ?? opened.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (source == null)
            {
                AppLog.Write("CALL", "webcam has no colour frame source");
                opened.Dispose();
                return;
            }

            // The format closest to the wanted size (never wider than 1280), preferring more frames per second.
            var target = Profiles[Profile].Pixels;
            var format = source.SupportedFormats
                .Where(f => f.VideoFormat.Width <= 1280)
                .OrderBy(f => Math.Abs((long)f.VideoFormat.Width * f.VideoFormat.Height - target))
                .ThenByDescending(f => (double)f.FrameRate.Numerator / Math.Max(1u, f.FrameRate.Denominator))
                .FirstOrDefault();
            if (format != null) await source.SetFormatAsync(format);

            var frameReader = await opened.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            frameReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            frameReader.FrameArrived += OnFrameArrived;
            var status = await frameReader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success || !running || mine != Volatile.Read(ref generation))
            {
                AppLog.Write("CALL", $"webcam reader not started: {status}");
                frameReader.FrameArrived -= OnFrameArrived;
                frameReader.Dispose();
                opened.Dispose();
                return;
            }
            capture = opened;
            reader = frameReader;
            AppLog.Write("CALL", $"webcam started {format?.VideoFormat.Width}x{format?.VideoFormat.Height}");
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "webcam start failed", ex);
            try { opened?.Dispose(); } catch { }
        }
    }

    void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!running) return;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref lastFrameTicks) < Profiles[Profile].GapMs) return;
        // One frame at a time: while a frame is being encoded the newer ones are dropped.
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        SoftwareBitmap? copy = null;
        try
        {
            using var reference = sender.TryAcquireLatestFrame();
            var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap == null)
            {
                Volatile.Write(ref busy, 0);
                return;
            }
            copy = SoftwareBitmap.Copy(bitmap);
            Interlocked.Exchange(ref lastFrameTicks, now);
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "reading a webcam frame failed", ex);
            Volatile.Write(ref busy, 0);
            return;
        }
        _ = EncodeAndSendAsync(copy);
    }

    async Task EncodeAndSendAsync(SoftwareBitmap bitmap)
    {
        try
        {
            var jpeg = await EncodeAsync(bitmap, Profiles[Profile].Jpeg);
            if (jpeg != null && running) onFrame?.Invoke(new VideoFrame(jpeg, 0));
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "encoding a webcam frame failed", ex);
        }
        finally
        {
            bitmap.Dispose();
            Volatile.Write(ref busy, 0);
        }
    }

    static async Task<byte[]?> EncodeAsync(SoftwareBitmap bitmap, int quality)
    {
        var source = bitmap;
        var converted = false;
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Ignore)
        {
            source = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            converted = true;
        }
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(quality / 100.0f, Windows.Foundation.PropertyType.Single) }
            };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
            encoder.SetSoftwareBitmap(source);
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            stream.Seek(0);
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
            return bytes;
        }
        finally
        {
            if (converted) source.Dispose();
        }
    }

    public void Stop()
    {
        running = false;
        Interlocked.Increment(ref generation);
        onFrame = null;
        var oldReader = reader;
        var oldCapture = capture;
        reader = null;
        capture = null;
        if (oldReader == null && oldCapture == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (oldReader != null)
                {
                    oldReader.FrameArrived -= OnFrameArrived;
                    await oldReader.StopAsync();
                    oldReader.Dispose();
                }
                oldCapture?.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Error("CALL", "stopping the webcam failed", ex);
            }
        });
    }
}
