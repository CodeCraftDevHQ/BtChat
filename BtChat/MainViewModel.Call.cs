using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BtChat;

public enum CallPhase
{
    Idle,
    Outgoing,
    Incoming,
    Active
}

public partial class MainViewModel
{
    const int RingSeconds = 45;

    ICallAudio callAudio = null!;
    ICallAlert callAlert = null!;
    ICallVideo callVideoDevice = null!;
    bool callFront = true;
    ChatSession? callSession;
    CancellationTokenSource? ringCts;
    IDispatcherTimer? callTimer;
    DateTime callStarted;
    volatile bool callActive;

    [ObservableProperty] CallPhase callState;
    [ObservableProperty] string callPeerName = "";
    [ObservableProperty] bool callMuted;
    [ObservableProperty] bool callSpeaker;
    [ObservableProperty] bool callVideo;
    [ObservableProperty] bool callCameraOn = true;

    public event Action<VideoFrame>? RemoteVideoFrame;
    public event Action<VideoFrame>? LocalVideoFrame;
    public event Action? RemoteVideoCleared;
    public event Action? LocalVideoCleared;
    public event Action<bool>? LocalMirrorChanged;

    public bool ShowCallButton => callAudio.IsSupported;
    public bool ShowVideoCallButton => callAudio.IsSupported && callVideoDevice.IsSupported;
    public bool ShowVideoStage => CallState == CallPhase.Active && CallVideo;
    public bool ShowAvatar => !ShowVideoStage;
    public bool ShowVideoControls => ShowVideoStage;
    public string CameraGlyph => CallCameraOn ? "📹" : "🚫";
    public bool ShowCall => CallState != CallPhase.Idle;
    public bool ShowAccept => CallState == CallPhase.Incoming;
    public bool ShowCallControls => CallState == CallPhase.Active;
    public string CallInitial => CallPeerName.Length > 0 ? CallPeerName[..1].ToUpperInvariant() : "B";
    public string MuteGlyph => CallMuted ? "🔇" : "🎤";
    public string SpeakerGlyph => CallSpeaker ? "🔊" : "📱";

    public string CallStatusText => CallState switch
    {
        CallPhase.Outgoing => Loc.Instance["callCalling"],
        CallPhase.Incoming => Loc.Instance[CallVideo ? "callVideoIncoming" : "callIncoming"],
        CallPhase.Active => FormatElapsed(DateTime.UtcNow - callStarted),
        _ => ""
    };

    static string FormatElapsed(TimeSpan span) => $"{(int)span.TotalMinutes}:{span.Seconds:00}";

    void InitCalls(ICallAudio audio, ICallAlert alert, ICallVideo video)
    {
        callAudio = audio;
        callAlert = alert;
        callVideoDevice = video;
    }

    partial void OnCallVideoChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowVideoStage));
        OnPropertyChanged(nameof(ShowAvatar));
        OnPropertyChanged(nameof(ShowVideoControls));
        OnPropertyChanged(nameof(CallStatusText));
    }

    partial void OnCallCameraOnChanged(bool value) => OnPropertyChanged(nameof(CameraGlyph));

    partial void OnCallStateChanged(CallPhase value)
    {
        OnPropertyChanged(nameof(ShowCall));
        OnPropertyChanged(nameof(ShowAccept));
        OnPropertyChanged(nameof(ShowCallControls));
        OnPropertyChanged(nameof(ShowVideoStage));
        OnPropertyChanged(nameof(ShowAvatar));
        OnPropertyChanged(nameof(ShowVideoControls));
        OnPropertyChanged(nameof(CallStatusText));
    }

    partial void OnCallPeerNameChanged(string value) => OnPropertyChanged(nameof(CallInitial));

    partial void OnCallMutedChanged(bool value)
    {
        callAudio.SetMuted(value);
        OnPropertyChanged(nameof(MuteGlyph));
    }

    partial void OnCallSpeakerChanged(bool value)
    {
        callAudio.SetSpeaker(value);
        OnPropertyChanged(nameof(SpeakerGlyph));
    }

    void HookCall(ChatSession current)
    {
        current.CallInvited += video => MainThread.BeginInvokeOnMainThread(() => OnCallInvited(current, video));
        current.CallAccepted += () => MainThread.BeginInvokeOnMainThread(() => OnCallAccepted(current));
        current.CallRejected += reason => MainThread.BeginInvokeOnMainThread(() => _ = OnCallRejectedAsync(current, reason));
        current.CallEnded += () => MainThread.BeginInvokeOnMainThread(() => OnCallEnded(current));
        current.CallAudioReceived += frame =>
        {
            if (callActive) callAudio.Play(frame);
        };
        current.CallVideoReceived += frame =>
        {
            if (!callActive || !CallVideo) return;
            if (frame == null) RemoteVideoCleared?.Invoke();
            else RemoteVideoFrame?.Invoke(frame);
        };
    }

    string CurrentPeerLabel(ChatSession s) => linkedChat?.Name ?? s.PeerName ?? "";

    [RelayCommand]
    Task StartCallAsync() => StartCallCoreAsync(false);

    [RelayCommand]
    Task StartVideoCallAsync() => StartCallCoreAsync(true);

    async Task StartCallCoreAsync(bool video)
    {
        var s = session;
        if (s == null || !CanSend || CallState != CallPhase.Idle || !callAudio.IsSupported) return;
        if (video)
        {
            if (!callVideoDevice.IsSupported) return;
            if (!s.IsWifi)
            {
                await ShowAlertAsync("videoNeedsWifi");
                return;
            }
        }
        if (!await permissions.EnsureAsync(PermissionKind.CallMicrophone)) return;
        if (video && !await permissions.EnsureAsync(PermissionKind.CallCamera)) return;
        s = session;
        if (s == null || !CanSend || CallState != CallPhase.Idle) return;
        StopAudio();
        callSession = s;
        CallPeerName = CurrentPeerLabel(s);
        CallVideo = video;
        CallState = CallPhase.Outgoing;
        try
        {
            await s.SendCallInviteAsync(video);
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "invite failed", ex);
            EndCallLocal();
            await ShowAlertAsync("callFailed");
            return;
        }
        StartRingTimeout(s);
    }

    void StartRingTimeout(ChatSession s)
    {
        ringCts?.Cancel();
        var cts = ringCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(RingSeconds), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (!ReferenceEquals(callSession, s) || CallState is CallPhase.Idle or CallPhase.Active) return;
                var wasOutgoing = CallState == CallPhase.Outgoing;
                await SendQuietly(() => wasOutgoing ? s.SendCallEndAsync() : s.SendCallRejectAsync(0));
                EndCallLocal();
                if (wasOutgoing) await ShowAlertAsync("callMissed");
            });
        });
    }

    static async Task SendQuietly(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "signal failed", ex);
        }
    }

    void OnCallInvited(ChatSession s, bool video)
    {
        if (CallState != CallPhase.Idle)
        {
            _ = SendQuietly(() => s.SendCallRejectAsync(1));
            return;
        }
        if (!callAudio.IsSupported || (video && !callVideoDevice.IsSupported))
        {
            _ = SendQuietly(() => s.SendCallRejectAsync(2));
            return;
        }
        StopAudio();
        callSession = s;
        CallPeerName = CurrentPeerLabel(s);
        CallVideo = video;
        CallState = CallPhase.Incoming;
        callAlert.StartRinging(CallPeerName);
        StartRingTimeout(s);
    }

    [RelayCommand]
    async Task AcceptCallAsync()
    {
        var s = callSession;
        if (s == null || CallState != CallPhase.Incoming) return;
        if (!await permissions.EnsureAsync(PermissionKind.CallMicrophone)
            || (CallVideo && !await permissions.EnsureAsync(PermissionKind.CallCamera)))
        {
            await DeclineCallAsync();
            return;
        }
        if (!ReferenceEquals(callSession, s) || CallState != CallPhase.Incoming) return;
        callAlert.StopRinging();
        try
        {
            await s.SendCallAcceptAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "accept failed", ex);
            EndCallLocal();
            await ShowAlertAsync("callFailed");
            return;
        }
        BeginActiveCall(s);
    }

    [RelayCommand]
    async Task DeclineCallAsync()
    {
        var s = callSession;
        var state = CallState;
        if (s == null || state == CallPhase.Idle) return;
        EndCallLocal();
        await SendQuietly(() => state == CallPhase.Incoming ? s.SendCallRejectAsync(0) : s.SendCallEndAsync());
    }

    void OnCallAccepted(ChatSession s)
    {
        if (!ReferenceEquals(callSession, s) || CallState != CallPhase.Outgoing) return;
        BeginActiveCall(s);
    }

    async Task OnCallRejectedAsync(ChatSession s, byte reason)
    {
        if (!ReferenceEquals(callSession, s) || CallState != CallPhase.Outgoing) return;
        EndCallLocal();
        await ShowAlertAsync(reason == 0 ? "callDeclined" : "callBusy");
    }

    void OnCallEnded(ChatSession s)
    {
        if (!ReferenceEquals(callSession, s) || CallState == CallPhase.Idle) return;
        EndCallLocal();
    }

    void BeginActiveCall(ChatSession s)
    {
        ringCts?.Cancel();
        callAlert.StopRinging();
        callStarted = DateTime.UtcNow;
        CallMuted = false;
        CallSpeaker = false;
        CallCameraOn = true;
        callFront = true;
        if (!callAudio.Start(frame => s.TrySendCallAudio(frame)))
        {
            _ = SendQuietly(() => s.SendCallEndAsync());
            EndCallLocal();
            _ = ShowAlertAsync("callFailed");
            return;
        }
        callActive = true;
        if (CallVideo)
        {
            var cameraStarted = false;
            try
            {
                cameraStarted = StartCamera(s);
            }
            catch (Exception ex)
            {
                AppLog.Error("CALL", "starting the camera failed", ex);
            }
            if (!cameraStarted)
            {
                _ = SendQuietly(() => s.SendCallEndAsync());
                EndCallLocal();
                _ = ShowAlertAsync("callFailed");
                return;
            }
            CallSpeaker = true;
            try { DeviceDisplay.Current.KeepScreenOn = true; } catch (Exception ex) { AppLog.Error("CALL", "keep screen on failed", ex); }
        }
        keepAlive.SetInCall(true, CallVideo);
        CallState = CallPhase.Active;
        if (callTimer == null)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                callTimer = dispatcher.CreateTimer();
                callTimer.Interval = TimeSpan.FromSeconds(1);
                callTimer.Tick += (_, _) => OnPropertyChanged(nameof(CallStatusText));
            }
        }
        callTimer?.Start();
    }

    void EndCallLocal()
    {
        ringCts?.Cancel();
        callTimer?.Stop();
        callActive = false;
        callAudio.Stop();
        callVideoDevice.Stop();
        callAlert.StopRinging();
        if (CallState == CallPhase.Active) keepAlive.SetInCall(false);
        if (CallVideo)
        {
            RemoteVideoCleared?.Invoke();
            LocalVideoCleared?.Invoke();
            try { DeviceDisplay.Current.KeepScreenOn = false; } catch { }
        }
        callSession = null;
        CallState = CallPhase.Idle;
        CallVideo = false;
        CallMuted = false;
        CallSpeaker = false;
    }

    bool StartCamera(ChatSession s)
    {
        LocalMirrorChanged?.Invoke(callFront);
        return callVideoDevice.Start(callFront, frame =>
        {
            LocalVideoFrame?.Invoke(frame);
            s.TrySendCallVideo(frame);
        });
    }

    [RelayCommand]
    void ToggleCamera()
    {
        var s = callSession;
        if (s == null || CallState != CallPhase.Active || !CallVideo) return;
        if (CallCameraOn)
        {
            CallCameraOn = false;
            callVideoDevice.Stop();
            _ = SendQuietly(() => s.SendCallVideoOffAsync());
            LocalVideoCleared?.Invoke();
        }
        else
        {
            CallCameraOn = StartCamera(s);
        }
    }

    [RelayCommand]
    void SwitchCamera()
    {
        var s = callSession;
        if (s == null || CallState != CallPhase.Active || !CallVideo || !CallCameraOn) return;
        callFront = !callFront;
        callVideoDevice.Stop();
        if (!StartCamera(s)) CallCameraOn = false;
    }

    [RelayCommand]
    void ToggleMute() => CallMuted = !CallMuted;

    [RelayCommand]
    void ToggleSpeaker() => CallSpeaker = !CallSpeaker;
}
