using CommunityToolkit.Mvvm.Input;

namespace BtChat;

public partial class MainViewModel
{
    IAudioPlayer audio = null!;
    ChatMessage? playing;
    IDispatcherTimer? audioTimer;
    float audioSpeed = 1f;

    public string AudioSpeedText => audioSpeed switch
    {
        > 1.75f => "2x",
        > 1.25f => "1.5x",
        _ => "1x"
    };

    void InitAudio(IAudioPlayer player)
    {
        audio = player;
        ChatMessage.InlineAudio = player.IsSupported;
        ChatMessage.DurationProbe = player.GetDurationAsync;
        audio.Completed += () => MainThread.BeginInvokeOnMainThread(OnAudioCompleted);
    }

    [RelayCommand]
    async Task ToggleAudioAsync(ChatMessage? message)
    {
        if (message == null || !message.ShowAudioPlayer || message.Location == null) return;
        if (ReferenceEquals(playing, message))
        {
            if (message.AudioPlaying)
            {
                audio.Pause();
                message.AudioPlaying = false;
            }
            else
            {
                audio.Resume(audioSpeed);
                message.AudioPlaying = true;
            }
            return;
        }
        StopAudio();
        playing = message;
        var start = message.AudioDuration > 0 && message.AudioPosition >= message.AudioDuration - 300 ? 0 : message.AudioPosition;
        try
        {
            message.AudioPlaying = true;
            StartAudioTimer();
            await audio.PlayAsync(message.Location, start, audioSpeed);
            if (!ReferenceEquals(playing, message)) return;
            if (message.AudioDuration == 0) message.AudioDuration = audio.Duration;
        }
        catch (Exception ex)
        {
            AppLog.Error("AUDIO", "playback failed", ex);
            if (ReferenceEquals(playing, message)) StopAudio();
            await ShowAlertAsync("playFailed");
        }
    }

    [RelayCommand]
    void CycleAudioSpeed()
    {
        audioSpeed = audioSpeed < 1.25f ? 1.5f : (audioSpeed < 1.75f ? 2f : 1f);
        OnPropertyChanged(nameof(AudioSpeedText));
        if (playing != null) audio.SetSpeed(audioSpeed);
    }

    public void SeekAudio(ChatMessage message, double fraction)
    {
        if (message.AudioDuration <= 0) return;
        var target = (long)(Math.Clamp(fraction, 0, 1) * message.AudioDuration);
        message.AudioPosition = target;
        if (ReferenceEquals(playing, message)) audio.Seek(target);
    }

    void StopAudio()
    {
        var message = playing;
        playing = null;
        audio.Stop();
        audioTimer?.Stop();
        if (message != null) message.AudioPlaying = false;
    }

    void OnAudioCompleted()
    {
        var message = playing;
        if (message == null) return;
        playing = null;
        audio.Stop();
        audioTimer?.Stop();
        message.AudioPlaying = false;
        message.AudioPosition = 0;
    }

    void StartAudioTimer()
    {
        if (audioTimer == null)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            audioTimer = dispatcher.CreateTimer();
            audioTimer.Interval = TimeSpan.FromMilliseconds(200);
            audioTimer.Tick += (_, _) => OnAudioTick();
        }
        audioTimer.Start();
    }

    void OnAudioTick()
    {
        var message = playing;
        if (message == null)
        {
            audioTimer?.Stop();
            return;
        }
        if (ChatOf(message) == null)
        {
            StopAudio();
            return;
        }
        if (message.AudioSeeking) return;
        var duration = audio.Duration;
        if (duration > 0) message.AudioDuration = duration;
        if (audio.IsPlaying || message.AudioPosition == 0) message.AudioPosition = audio.Position;
    }
}
