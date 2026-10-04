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
    ChatSession? callSession;
    CancellationTokenSource? ringCts;
    IDispatcherTimer? callTimer;
    DateTime callStarted;
    volatile bool callActive;

    [ObservableProperty] CallPhase callState;
    [ObservableProperty] string callPeerName = "";
    [ObservableProperty] bool callMuted;
    [ObservableProperty] bool callSpeaker;

    public bool ShowCallButton => callAudio.IsSupported;
    public bool ShowCall => CallState != CallPhase.Idle;
    public bool ShowAccept => CallState == CallPhase.Incoming;
    public bool ShowCallControls => CallState == CallPhase.Active;
    public string CallInitial => CallPeerName.Length > 0 ? CallPeerName[..1].ToUpperInvariant() : "B";
    public string MuteGlyph => CallMuted ? "🔇" : "🎤";
    public string SpeakerGlyph => CallSpeaker ? "🔊" : "📱";

    public string CallStatusText => CallState switch
    {
        CallPhase.Outgoing => Loc.Instance["callCalling"],
        CallPhase.Incoming => Loc.Instance["callIncoming"],
        CallPhase.Active => FormatElapsed(DateTime.UtcNow - callStarted),
        _ => ""
    };

    static string FormatElapsed(TimeSpan span) => $"{(int)span.TotalMinutes}:{span.Seconds:00}";

    void InitCalls(ICallAudio audio, ICallAlert alert)
    {
        callAudio = audio;
        callAlert = alert;
    }

    partial void OnCallStateChanged(CallPhase value)
    {
        OnPropertyChanged(nameof(ShowCall));
        OnPropertyChanged(nameof(ShowAccept));
        OnPropertyChanged(nameof(ShowCallControls));
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
    }

    string CurrentPeerLabel(ChatSession s) => linkedChat?.Name ?? s.PeerName ?? "";

    [RelayCommand]
    async Task StartCallAsync()
    {
        var s = session;
        if (s == null || !CanSend || CallState != CallPhase.Idle || !callAudio.IsSupported) return;
        if (!await permissions.EnsureAsync(PermissionKind.CallMicrophone)) return;
        s = session;
        if (s == null || !CanSend || CallState != CallPhase.Idle) return;
        StopAudio();
        callSession = s;
        CallPeerName = CurrentPeerLabel(s);
        CallState = CallPhase.Outgoing;
        try
        {
            await s.SendCallInviteAsync(false);
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
        if (video || !callAudio.IsSupported)
        {
            _ = SendQuietly(() => s.SendCallRejectAsync(2));
            return;
        }
        StopAudio();
        callSession = s;
        CallPeerName = CurrentPeerLabel(s);
        CallState = CallPhase.Incoming;
        callAlert.StartRinging(CallPeerName);
        StartRingTimeout(s);
    }

    [RelayCommand]
    async Task AcceptCallAsync()
    {
        var s = callSession;
        if (s == null || CallState != CallPhase.Incoming) return;
        if (!await permissions.EnsureAsync(PermissionKind.CallMicrophone))
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
        if (!callAudio.Start(frame => s.TrySendCallAudio(frame)))
        {
            _ = SendQuietly(() => s.SendCallEndAsync());
            EndCallLocal();
            _ = ShowAlertAsync("callFailed");
            return;
        }
        callActive = true;
        keepAlive.SetInCall(true);
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
        callAlert.StopRinging();
        if (CallState == CallPhase.Active) keepAlive.SetInCall(false);
        callSession = null;
        CallState = CallPhase.Idle;
        CallMuted = false;
        CallSpeaker = false;
    }

    [RelayCommand]
    void ToggleMute() => CallMuted = !CallMuted;

    [RelayCommand]
    void ToggleSpeaker() => CallSpeaker = !CallSpeaker;
}
