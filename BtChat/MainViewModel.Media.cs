using CommunityToolkit.Mvvm.ComponentModel;

namespace BtChat;

public partial class MainViewModel
{
    const int HoldMs = 1000;
    const double SlideCancelDp = 100;
    const int MaxVoiceSeconds = 600;
    const double MinVoiceSeconds = 0.5;

    IVoiceRecorder recorder = null!;
    CancellationTokenSource? holdCts;
    bool pressed;
    bool holdReached;
    string? recordingPath;

    [ObservableProperty] bool isCameraMode;
    [ObservableProperty] bool isRecording;
    [ObservableProperty] string recordTime = "0:00";
    [ObservableProperty] bool recordWillCancel;

    public bool HasDraft => Draft.Trim().Length > 0;
    public bool ShowMediaButton => recorder.IsSupported && !HasDraft;
    public bool ShowSend => !recorder.IsSupported || HasDraft;
    public bool NotRecording => !IsRecording;
    public string MediaButtonText => IsCameraMode ? "📷" : "🎤";
    public string RecordHint => Loc.Instance[RecordWillCancel ? "recordReleaseCancel" : "recordSlideCancel"];

    partial void OnDraftChanged(string value)
    {
        OnPropertyChanged(nameof(HasDraft));
        OnPropertyChanged(nameof(ShowMediaButton));
        OnPropertyChanged(nameof(ShowSend));
    }

    partial void OnIsCameraModeChanged(bool value) => OnPropertyChanged(nameof(MediaButtonText));

    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(NotRecording));

    partial void OnRecordWillCancelChanged(bool value) => OnPropertyChanged(nameof(RecordHint));

    public void MediaButtonDown()
    {
        if (!CanSend || IsRecording) return;
        pressed = true;
        holdReached = false;
        RecordWillCancel = false;
        holdCts?.Cancel();
        var cts = holdCts = new CancellationTokenSource();
        _ = HoldTimerAsync(cts.Token);
    }

    async Task HoldTimerAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(HoldMs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await MainThread.InvokeOnMainThreadAsync(BeginHoldAsync);
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "hold action failed", ex);
        }
    }

    async Task BeginHoldAsync()
    {
        if (!pressed) return;
        holdReached = true;
        if (IsCameraMode)
        {
            await CaptureAndSendAsync();
            return;
        }
        await StartVoiceAsync();
    }

    public void MediaButtonMoved(double dx)
    {
        if (!IsRecording) return;
        RecordWillCancel = Math.Abs(dx) > SlideCancelDp;
    }

    public void MediaButtonUp(bool cancelled)
    {
        if (!pressed) return;
        pressed = false;
        holdCts?.Cancel();
        if (!holdReached)
        {
            if (!cancelled) OnMediaTap();
            return;
        }
        if (IsRecording) _ = EndVoiceAsync(send: !cancelled && !RecordWillCancel);
    }

    void OnMediaTap()
    {
        IsCameraMode = !IsCameraMode;
    }

    async Task StartVoiceAsync()
    {
        if (!await permissions.EnsureAsync(PermissionKind.Microphone)) return;
        if (!pressed || !CanSend) return;
        StopAudio();
        var path = Path.Combine(FileSystem.AppDataDirectory, "voice", $"voice_{DateTime.Now:yyyyMMdd_HHmmss}.m4a");
        if (!recorder.Start(path))
        {
            await ShowAlertAsync("recordFailed");
            return;
        }
        recordingPath = path;
        RecordTime = "0:00";
        IsRecording = true;
        var started = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            while (IsRecording)
            {
                var elapsed = DateTime.UtcNow - started;
                MainThread.BeginInvokeOnMainThread(() => RecordTime = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}");
                if (elapsed.TotalSeconds >= MaxVoiceSeconds)
                {
                    MainThread.BeginInvokeOnMainThread(() => _ = EndVoiceAsync(send: true));
                    return;
                }
                await Task.Delay(250);
            }
        });
    }

    async Task EndVoiceAsync(bool send)
    {
        if (!IsRecording) return;
        var path = recordingPath;
        recordingPath = null;
        IsRecording = false;
        RecordWillCancel = false;
        var length = recorder.Stop(keep: send);
        if (path == null) return;
        if (!send || length.TotalSeconds < MinVoiceSeconds)
        {
            try { File.Delete(path); } catch { }
            return;
        }
        await SendCapturedAsync(Path.GetFileName(path), path, new FileInfo(path).Length);
    }

    async Task CaptureAndSendAsync()
    {
        try
        {
            if (!CanSend) return;
            if (!await permissions.EnsureAsync(PermissionKind.CameraCapture)) return;
            var captured = await MediaCapture.CaptureAsync(video: true);
            if (captured == null) return;
            await SendCapturedAsync(captured.Value.Name, captured.Value.Path, captured.Value.Size);
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "camera capture failed", ex);
            await ShowAlertAsync("captureFailed");
        }
    }

    async Task SendCapturedAsync(string name, string path, long size)
    {
        var chat = TargetChat();
        var s = session;
        if (s != null && CanSend)
        {
            var file = new PickedFile(name, path, size, () => fileSource.OpenAsync(path));
            await EnqueueFilesAsync(s, chat, new[] { file });
            return;
        }
        await QueueOfflineAsync(chat, new[] { new SharedFile(name, path, size) });
    }

    static async Task ShowAlertAsync(string messageKey)
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        var loc = Loc.Instance;
        await page.DisplayAlert("BtChat", loc[messageKey], loc["ok"]);
    }
}
