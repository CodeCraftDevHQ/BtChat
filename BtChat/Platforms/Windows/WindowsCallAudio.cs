using NAudio.Wave;

namespace BtChat;

// Voice for calls on Windows: microphone and speakers through the default devices (NAudio / WinMM).
// 16 kHz mono, 40 ms frames of mu-law (640 bytes), the same as on Android.
// There is no echo cancellation here, so a headset gives the best result.
public sealed class WindowsCallAudio : ICallAudio
{
    const int SampleRate = 16000;
    const int FrameSamples = 640;           // 40 ms
    const int FrameBytes = FrameSamples * 2; // 16-bit samples

    static readonly WaveFormat Format = new(SampleRate, 16, 1);

    readonly object gate = new();
    WaveInEvent? input;
    WaveOutEvent? output;
    BufferedWaveProvider? playback;
    Action<byte[]>? onCaptured;
    readonly byte[] pending = new byte[FrameBytes * 4];
    int pendingLength;
    volatile bool muted;

    public bool IsSupported => true;

    public bool Start(Action<byte[]> onCaptured)
    {
        Stop();
        try
        {
            if (WaveInEvent.DeviceCount == 0)
            {
                AppLog.Write("CALL", "no microphone found");
                return false;
            }
            lock (gate)
            {
                this.onCaptured = onCaptured;
                pendingLength = 0;
                muted = false;

                playback = new BufferedWaveProvider(Format)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(1)
                };
                output = new WaveOutEvent { DesiredLatency = 120 };
                output.Init(playback);
                output.Play();

                input = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = 40, NumberOfBuffers = 3 };
                input.DataAvailable += OnData;
                input.StartRecording();
            }
            AppLog.Write("CALL", "windows audio started");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "windows audio start failed", ex);
            Stop();
            return false;
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var callback = onCaptured;
        if (callback == null) return;
        // The recorder delivers chunks of any size: cut them into exact 40 ms frames.
        var offset = 0;
        while (offset < e.BytesRecorded)
        {
            var take = Math.Min(FrameBytes - pendingLength, e.BytesRecorded - offset);
            Buffer.BlockCopy(e.Buffer, offset, pending, pendingLength, take);
            pendingLength += take;
            offset += take;
            if (pendingLength < FrameBytes) continue;
            pendingLength = 0;
            if (muted) continue;
            var frame = new byte[FrameSamples];
            for (var i = 0; i < FrameSamples; i++)
                frame[i] = MuLaw.Encode(BitConverter.ToInt16(pending, i * 2));
            try
            {
                callback(frame);
            }
            catch (Exception ex)
            {
                AppLog.Error("CALL", "sending an audio frame failed", ex);
            }
        }
    }

    public void Play(byte[] frame)
    {
        var buffer = playback;
        if (buffer == null || frame.Length == 0) return;
        var pcm = new byte[frame.Length * 2];
        for (var i = 0; i < frame.Length; i++)
        {
            var sample = MuLaw.Decode(frame[i]);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        try
        {
            buffer.AddSamples(pcm, 0, pcm.Length);
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "playing an audio frame failed", ex);
        }
    }

    public void SetMuted(bool muted) => this.muted = muted;

    // Windows uses the default playback device; there is no earpiece/speaker switch.
    public void SetSpeaker(bool speaker)
    {
    }

    public void Stop()
    {
        lock (gate)
        {
            onCaptured = null;
            try
            {
                if (input != null)
                {
                    input.DataAvailable -= OnData;
                    input.StopRecording();
                    input.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("CALL", "stopping the microphone failed", ex);
            }
            try
            {
                output?.Stop();
                output?.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Error("CALL", "stopping playback failed", ex);
            }
            input = null;
            output = null;
            playback = null;
            pendingLength = 0;
        }
    }
}
