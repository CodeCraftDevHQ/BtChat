using System.Collections.Concurrent;
using Android.Content;
using Android.Media;
using Android.Media.Audiofx;

namespace BtChat;

public sealed class AndroidCallAudio : ICallAudio
{
    const int Rate = 16000;
    const int FrameSamples = 640;

    AudioRecord? record;
    AudioTrack? track;
    AcousticEchoCanceler? echo;
    NoiseSuppressor? noise;
    Thread? captureThread;
    Thread? playThread;
    BlockingCollection<byte[]>? queue;
    AudioManager? manager;
    Mode previousMode;
    bool previousSpeaker;
    volatile bool running;
    volatile bool muted;

    public bool IsSupported => true;

#pragma warning disable CA1422
    public bool Start(Action<byte[]> onCaptured)
    {
        Stop();
        try
        {
            var context = Android.App.Application.Context;
            manager = context.GetSystemService(Context.AudioService) as AudioManager;
            if (manager != null)
            {
                previousMode = manager.Mode;
                previousSpeaker = manager.SpeakerphoneOn;
                manager.Mode = Mode.InCommunication;
                manager.SpeakerphoneOn = false;
            }

            var minRecord = AudioRecord.GetMinBufferSize(Rate, ChannelIn.Mono, Encoding.Pcm16bit);
            record = new AudioRecord(AudioSource.VoiceCommunication, Rate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(minRecord, FrameSamples * 8));
            if (record.State != State.Initialized) throw new InvalidOperationException("microphone not available");
            try
            {
                if (AcousticEchoCanceler.IsAvailable)
                {
                    echo = AcousticEchoCanceler.Create(record.AudioSessionId);
                    echo?.SetEnabled(true);
                }
                if (NoiseSuppressor.IsAvailable)
                {
                    noise = NoiseSuppressor.Create(record.AudioSessionId);
                    noise?.SetEnabled(true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("CALL", "audio effects failed", ex);
            }

            var minTrack = AudioTrack.GetMinBufferSize(Rate, ChannelOut.Mono, Encoding.Pcm16bit);
            track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.VoiceCommunication)!
                    .SetContentType(AudioContentType.Speech)!
                    .Build()!)!
                .SetAudioFormat(new AudioFormat.Builder()
                    .SetEncoding(Encoding.Pcm16bit)!
                    .SetSampleRate(Rate)!
                    .SetChannelMask(ChannelOut.Mono)!
                    .Build()!)!
                .SetBufferSizeInBytes(Math.Max(minTrack, FrameSamples * 2 * 6))!
                .SetTransferMode(AudioTrackMode.Stream)!
                .Build()!;

            queue = new BlockingCollection<byte[]>(15);
            muted = false;
            running = true;
            record.StartRecording();
            track.Play();
            captureThread = new Thread(() => CaptureLoop(onCaptured)) { IsBackground = true };
            playThread = new Thread(PlayLoop) { IsBackground = true };
            captureThread.Start();
            playThread.Start();
            AppLog.Write("CALL", "audio started");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "audio start failed", ex);
            Stop();
            return false;
        }
    }

    void CaptureLoop(Action<byte[]> onCaptured)
    {
        try
        {
            Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        }
        catch
        {
        }
        var pcm = new short[FrameSamples];
        while (running)
        {
            var current = record;
            if (current == null) break;
            var read = 0;
            while (read < FrameSamples && running)
            {
                var count = current.Read(pcm, read, FrameSamples - read);
                if (count <= 0)
                {
                    running = false;
                    break;
                }
                read += count;
            }
            if (!running || muted) continue;
            var frame = new byte[FrameSamples];
            for (var i = 0; i < FrameSamples; i++) frame[i] = MuLaw.Encode(pcm[i]);
            try
            {
                onCaptured(frame);
            }
            catch (Exception ex)
            {
                AppLog.Error("CALL", "sending a frame failed", ex);
            }
        }
    }

    void PlayLoop()
    {
        try
        {
            Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        }
        catch
        {
        }
        var pcm = new short[FrameSamples];
        var primed = false;
        while (running)
        {
            var frames = queue;
            var output = track;
            if (frames == null || output == null) break;
            if (!primed)
            {
                var waited = 0;
                while (running && frames.Count < 2 && waited < 120)
                {
                    Thread.Sleep(10);
                    waited += 10;
                }
                primed = true;
            }
            byte[]? frame;
            try
            {
                if (!frames.TryTake(out frame, 200)) continue;
            }
            catch
            {
                break;
            }
            var length = Math.Min(frame.Length, FrameSamples);
            for (var i = 0; i < length; i++) pcm[i] = MuLaw.Decode(frame[i]);
            output.Write(pcm, 0, length);
        }
    }

    public void Play(byte[] frame)
    {
        var frames = queue;
        if (!running || frames == null) return;
        try
        {
            if (!frames.TryAdd(frame))
            {
                frames.TryTake(out _);
                frames.TryAdd(frame);
            }
        }
        catch
        {
        }
    }

    public void SetMuted(bool muted) => this.muted = muted;

    public void SetSpeaker(bool speaker)
    {
        if (manager != null) manager.SpeakerphoneOn = speaker;
    }

    public void Stop()
    {
        running = false;
        try { queue?.CompleteAdding(); } catch { }
        var capture = captureThread;
        var play = playThread;
        captureThread = null;
        playThread = null;
        try { capture?.Join(500); } catch { }
        try { play?.Join(500); } catch { }
        try { record?.Stop(); } catch { }
        try { record?.Release(); } catch { }
        try { track?.Stop(); } catch { }
        try { track?.Release(); } catch { }
        try { echo?.Release(); } catch { }
        try { noise?.Release(); } catch { }
        record = null;
        track = null;
        echo = null;
        noise = null;
        queue = null;
        if (manager != null)
        {
            try
            {
                manager.SpeakerphoneOn = previousSpeaker;
                manager.Mode = previousMode;
            }
            catch
            {
            }
            manager = null;
        }
    }
#pragma warning restore CA1422
}
