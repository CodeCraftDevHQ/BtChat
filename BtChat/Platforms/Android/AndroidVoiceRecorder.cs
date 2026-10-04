using Android.Media;

namespace BtChat;

public sealed class AndroidVoiceRecorder : IVoiceRecorder
{
    MediaRecorder? recorder;
    string? path;
    DateTime startedAt;

    public bool IsSupported => true;

    public bool Start(string path)
    {
        Cleanup();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
#pragma warning disable CA1422
            var created = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? new MediaRecorder(Android.App.Application.Context)
                : new MediaRecorder();
#pragma warning restore CA1422
            recorder = created;
            created.SetAudioSource(AudioSource.Mic);
            created.SetOutputFormat(OutputFormat.Mpeg4);
            created.SetAudioEncoder(AudioEncoder.Aac);
            created.SetAudioEncodingBitRate(64000);
            created.SetAudioSamplingRate(44100);
            created.SetOutputFile(path);
            created.Prepare();
            created.Start();
            this.path = path;
            startedAt = DateTime.UtcNow;
            AppLog.Write("VOICE", $"recording started {path}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("VOICE", "start recording failed", ex);
            Cleanup();
            TryDelete(path);
            return false;
        }
    }

    public TimeSpan Stop(bool keep)
    {
        var current = recorder;
        var file = path;
        var length = DateTime.UtcNow - startedAt;
        if (current == null) return TimeSpan.Zero;
        try
        {
            current.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Error("VOICE", "stop recording failed (probably too short)", ex);
            keep = false;
        }
        Cleanup();
        if (!keep) TryDelete(file);
        AppLog.Write("VOICE", $"recording stopped keep={keep} length={length.TotalSeconds:0.0}s");
        return keep ? length : TimeSpan.Zero;
    }

    void Cleanup()
    {
        try
        {
            recorder?.Release();
        }
        catch
        {
        }
        recorder?.Dispose();
        recorder = null;
        path = null;
    }

    static void TryDelete(string? file)
    {
        if (file == null) return;
        try
        {
            File.Delete(file);
        }
        catch
        {
        }
    }
}
