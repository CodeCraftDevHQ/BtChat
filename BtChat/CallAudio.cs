namespace BtChat;

public interface ICallAudio
{
    bool IsSupported { get; }
    // Starts capturing and playing. Every captured 40 ms frame (640 bytes, mu-law, 16 kHz mono) goes to onCaptured.
    bool Start(Action<byte[]> onCaptured);
    void Play(byte[] frame);
    void SetMuted(bool muted);
    void SetSpeaker(bool speaker);
    void Stop();
}

public interface ICallAlert
{
    void StartRinging(string name);
    void StopRinging();
}

public static class MuLaw
{
    const int Bias = 0x84;
    const int Clip = 32635;

    public static byte Encode(short sample)
    {
        int value = sample;
        var sign = (value >> 8) & 0x80;
        if (sign != 0) value = -value;
        if (value > Clip) value = Clip;
        value += Bias;
        var exponent = 7;
        for (var mask = 0x4000; (value & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
        var mantissa = (value >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    public static short Decode(byte code)
    {
        code = (byte)~code;
        var sign = code & 0x80;
        var exponent = (code >> 4) & 0x07;
        var mantissa = code & 0x0F;
        var value = ((mantissa << 3) + Bias) << exponent;
        value -= Bias;
        return (short)(sign != 0 ? -value : value);
    }
}

#if !ANDROID
public sealed class NoCallAudio : ICallAudio
{
    public bool IsSupported => false;
    public bool Start(Action<byte[]> onCaptured) => false;
    public void Play(byte[] frame) { }
    public void SetMuted(bool muted) { }
    public void SetSpeaker(bool speaker) { }
    public void Stop() { }
}

public sealed class NoCallAlert : ICallAlert
{
    public void StartRinging(string name) { }
    public void StopRinging() { }
}
#endif
