using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Views;

namespace BtChat;

public sealed class AndroidCallVideo : ICallVideo
{
    const int TargetPixels = 480 * 360;
    const int MinFrameGapMs = 80;
    const int JpegQuality = 50;

    CameraDevice? device;
    CameraCaptureSession? session;
    ImageReader? reader;
    HandlerThread? thread;
    Handler? handler;
    Action<VideoFrame>? onFrame;
    int sensorOrientation;
    bool front;
    long lastFrameTicks;
    volatile bool running;

    public bool IsSupported => true;

    public bool Start(bool front, Action<VideoFrame> onFrame)
    {
        Stop();
        try
        {
            var context = Android.App.Application.Context;
            var manager = (CameraManager)context.GetSystemService(Context.CameraService)!;
            string? cameraId = null;
            CameraCharacteristics? characteristics = null;
            var wanted = front ? (int)LensFacing.Front : (int)LensFacing.Back;
            foreach (var id in manager.GetCameraIdList()!)
            {
                var candidate = manager.GetCameraCharacteristics(id);
                var facing = ((Java.Lang.Integer?)candidate.Get(CameraCharacteristics.LensFacing))?.IntValue();
                if (facing != wanted) continue;
                cameraId = id;
                characteristics = candidate;
                break;
            }
            if (cameraId == null || characteristics == null) return false;

            this.front = front;
            this.onFrame = onFrame;
            sensorOrientation = ((Java.Lang.Integer?)characteristics.Get(CameraCharacteristics.SensorOrientation))?.IntValue() ?? 90;
            var map = (StreamConfigurationMap?)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
            var sizes = map?.GetOutputSizes((int)ImageFormatType.Yuv420888);
            if (sizes == null || sizes.Length == 0) return false;
            var best = sizes[0];
            var bestScore = long.MaxValue;
            foreach (var size in sizes)
            {
                if (size.Width > 1280) continue;
                var score = Math.Abs((long)size.Width * size.Height - TargetPixels);
                if (score >= bestScore) continue;
                best = size;
                bestScore = score;
            }

            thread = new HandlerThread("btchat-camera");
            thread.Start();
            handler = new Handler(thread.Looper!);
            reader = ImageReader.NewInstance(best.Width, best.Height, ImageFormatType.Yuv420888, 2)!;
            reader.SetOnImageAvailableListener(new ImageListener(this), handler);
            running = true;
            manager.OpenCamera(cameraId, new CameraStateCallback(this), handler);
            AppLog.Write("CALL", $"camera opening front={front} {best.Width}x{best.Height}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "camera start failed", ex);
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        running = false;
        try { session?.Close(); } catch { }
        try { device?.Close(); } catch { }
        try { reader?.Close(); } catch { }
        session = null;
        device = null;
        reader = null;
        onFrame = null;
        try { thread?.QuitSafely(); } catch { }
        thread = null;
        handler = null;
    }

    void OnOpened(CameraDevice camera)
    {
        if (!running || reader == null)
        {
            camera.Close();
            return;
        }
        device = camera;
        try
        {
            camera.CreateCaptureSession(new List<Surface> { reader.Surface! }, new SessionStateCallback(this), handler);
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "camera session failed", ex);
        }
    }

    void OnConfigured(CameraCaptureSession configured)
    {
        if (!running || device == null || reader == null)
        {
            configured.Close();
            return;
        }
        session = configured;
        try
        {
            var request = device.CreateCaptureRequest(CameraTemplate.Record)!;
            request.AddTarget(reader.Surface!);
            request.Set(CaptureRequest.ControlAfMode, new Java.Lang.Integer((int)ControlAFMode.ContinuousVideo));
            configured.SetRepeatingRequest(request.Build()!, null, handler);
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "camera request failed", ex);
        }
    }

    void OnImage(ImageReader source)
    {
        using var image = source.AcquireLatestImage();
        if (image == null) return;
        if (!running) return;
        var now = Environment.TickCount64;
        if (now - lastFrameTicks < MinFrameGapMs) return;
        lastFrameTicks = now;
        var callback = onFrame;
        if (callback == null) return;
        try
        {
            var width = image.Width;
            var height = image.Height;
            var nv21 = ToNv21(image, width, height);
            using var yuv = new YuvImage(nv21, ImageFormatType.Nv21, width, height, null);
            using var output = new MemoryStream();
            yuv.CompressToJpeg(new Rect(0, 0, width, height), JpegQuality, output);
            callback(new VideoFrame(output.ToArray(), (byte)(UprightDegrees() / 90)));
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "camera frame failed", ex);
        }
    }

#pragma warning disable CA1422
    int UprightDegrees()
    {
        var rotation = Platform.CurrentActivity?.WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;
        var display = rotation switch
        {
            SurfaceOrientation.Rotation90 => 90,
            SurfaceOrientation.Rotation180 => 180,
            SurfaceOrientation.Rotation270 => 270,
            _ => 0
        };
        return front ? (sensorOrientation + display) % 360 : (sensorOrientation - display + 360) % 360;
    }
#pragma warning restore CA1422

    static byte[] ToNv21(Image image, int width, int height)
    {
        var planes = image.GetPlanes()!;
        var nv21 = new byte[width * height * 3 / 2];
        var y = Read(planes[0].Buffer!);
        var yRow = planes[0].RowStride;
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(y, row * yRow, nv21, row * width, width);

        var u = Read(planes[1].Buffer!);
        var v = Read(planes[2].Buffer!);
        var uRow = planes[1].RowStride;
        var uPixel = planes[1].PixelStride;
        var vRow = planes[2].RowStride;
        var vPixel = planes[2].PixelStride;
        var offset = width * height;
        for (var row = 0; row < height / 2; row++)
        {
            for (var col = 0; col < width / 2; col++)
            {
                nv21[offset++] = v[row * vRow + col * vPixel];
                nv21[offset++] = u[row * uRow + col * uPixel];
            }
        }
        return nv21;
    }

    static byte[] Read(Java.Nio.ByteBuffer buffer)
    {
        var data = new byte[buffer.Remaining()];
        buffer.Get(data);
        return data;
    }

    sealed class ImageListener(AndroidCallVideo owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader != null) owner.OnImage(reader);
        }
    }

    sealed class CameraStateCallback(AndroidCallVideo owner) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => owner.OnOpened(camera);

        public override void OnDisconnected(CameraDevice camera) => camera.Close();

        public override void OnError(CameraDevice camera, CameraError error)
        {
            AppLog.Write("CALL", $"camera error {error}");
            camera.Close();
        }
    }

    sealed class SessionStateCallback(AndroidCallVideo owner) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => owner.OnConfigured(session);

        public override void OnConfigureFailed(CameraCaptureSession session) => AppLog.Write("CALL", "camera configure failed");
    }
}
