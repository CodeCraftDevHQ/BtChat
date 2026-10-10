using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BtChat;

public partial class MainViewModel : ObservableObject
{
    readonly IBluetoothTransport transport;
    readonly TcpTransport tcp;
    readonly IReceivedFileStore files;
    readonly IQrScanner qr;
    readonly DiscoveryService discovery;
    readonly IKeepAlive keepAlive;
    readonly ProxyViewModel proxy;
    readonly IFileSource fileSource;
    readonly IPermissionGate permissions;
    bool btStarted;
    int logRefreshPending;
    readonly object sendLock = new();
    readonly ResumeRegistry resumes = new();
    readonly List<PendingSend> sendQueue = new();
    int activeSends;
    const int MaxParallelSends = 4;
    QrShowPage? qrPage;
    bool searched;
    readonly object gate = new();
    readonly SemaphoreSlim connectLock = new(1, 1);
    ChatSession? session;
    // True while a new connection is agreeing on encryption (before the chat session exists).
    bool negotiating;
    BtDevice? autoTarget;
    string statusKey = "idle";
    bool started;

    const string UnknownId = "unknown";
    readonly ObservableCollection<ChatMessage> noMessages = new();
    Conversation? linkedChat;
    CancellationTokenSource? helloCts;

    public ObservableCollection<Conversation> Conversations { get; } = new();
    public ObservableCollection<ChatMessage> Messages => CurrentChat?.Messages ?? noMessages;
    public event Action? ScrollRequested;
    public event Action<ChatMessage>? ScrollToMessageRequested;
    public event Action? FocusDraftRequested;

    [ObservableProperty] Conversation? currentChat;
    [ObservableProperty] bool isDrawerOpen;
    [ObservableProperty] bool isWide;

    public bool ShowMenuButton => !IsWide;
    public bool ShowScrim => IsDrawerOpen && !IsWide;

    partial void OnIsDrawerOpenChanged(bool value) => OnPropertyChanged(nameof(ShowScrim));

    partial void OnIsWideChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMenuButton));
        OnPropertyChanged(nameof(ShowScrim));
    }
    [ObservableProperty] string myName = LocalDevice.Name;

    [ObservableProperty] double bubbleMaxWidth = 420;

    // ---- settings page ---------------------------------------------------------------------------

    public string[] LanguageItems { get; } = { "فارسی", "English" };
    public string[] ThemeItems => new[] { Loc.Instance["themeAuto"], Loc.Instance["themeLight"], Loc.Instance["themeDark"] };

    [ObservableProperty] int languageIndex = Loc.Instance.IsFa ? 0 : 1;
    [ObservableProperty] int themeIndex = Math.Clamp(Preferences.Default.Get("theme", 0), 0, 2);

    partial void OnLanguageIndexChanged(int value)
    {
        var wantFa = value == 0;
        if (Loc.Instance.IsFa != wantFa) ToggleLanguage();
    }

    partial void OnThemeIndexChanged(int value)
    {
        Preferences.Default.Set("theme", value);
        ApplyTheme();
    }

    static void ApplyTheme()
    {
        var app = Application.Current;
        if (app == null) return;
        app.UserAppTheme = Preferences.Default.Get("theme", 0) switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
    }

    [RelayCommand]
    async Task OpenSettingsAsync()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        await SafeAsync("open settings", () => page.Navigation.PushModalAsync(new SettingsPage(this)));
    }

    public string CurrentTitle => CurrentChat?.Name ?? "BtChat";
    public string CurrentInitial => CurrentChat?.Initial ?? "B";
    // Green: connected, orange: trying to reconnect, gray: not connected.
    public Color ConnectionDot => IsConnected ? Color.FromArgb("#22C55E") : (IsLinked ? Color.FromArgb("#F59E0B") : Colors.Gray);
    public bool HasChats => Conversations.Count > 0;
    public bool HasNoChats => Conversations.Count == 0;
    public bool CanSend => IsConnected && (linkedChat == null || ReferenceEquals(CurrentChat, linkedChat));
    public string DraftPlaceholder => Loc.Instance[CanSend ? "type" : "notLinkedHere"];
    public ObservableCollection<BtDevice> Devices { get; } = new();

    [ObservableProperty] BtDevice? selectedDevice;
    [ObservableProperty] string status = "";
    [ObservableProperty] string draft = "";
    [ObservableProperty] bool isConnected;
    [ObservableProperty] string host = Preferences.Default.Get("host", "");
    [ObservableProperty] bool showLog;
    [ObservableProperty] string logText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBluetoothMode))]
    [NotifyPropertyChangedFor(nameof(IsLanMode))]
    int modeIndex = Preferences.Default.Get("mode", 0) == 1 ? 1 : 0;

    public string[] ModeItems { get; } = { "Bluetooth", "Wi-Fi" };

    // The "Manual connection" drawer in the Wi-Fi card is closed until the user opens it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualArrow))]
    bool manualOpen;

    public string ManualArrow => ManualOpen ? "▴" : "▾";

    [RelayCommand]
    void ToggleManual() => ManualOpen = !ManualOpen;

    // Show QR fills the whole row when this device cannot scan (Windows).
    public int QrTileSpan => CanScanQr ? 1 : 2;

    public string LinkedTitle => IsConnected
        ? string.Format(Loc.Instance["connectedTo"], linkedChat?.Name ?? "")
        : Loc.Instance["retrying"];

    public bool IsBluetoothMode => ModeIndex == 0;
    public bool IsLanMode => ModeIndex == 1;
    public ObservableCollection<FoundDevice> FoundDevices { get; } = new();
    public bool HasFound => FoundDevices.Count > 0;
    public bool CanScanQr => qr.IsSupported;
    public bool IsNotSearching => !IsSearching;
    public string SearchText => IsSearching
        ? Loc.Instance["searching"]
        : (searched && FoundDevices.Count == 0 ? Loc.Instance["noneFound"] : "");
    public bool HasSearchText => SearchText.Length > 0;
    [ObservableProperty] bool isSearching;

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotSearching));
        NotifySearch();
    }

    void NotifySearch()
    {
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
    }

    readonly IMessageAlert messageAlert;

    // Notification for a new message or file while the app is not on screen.
    [ObservableProperty] bool messageNotifications = Preferences.Default.Get("notifyMessages", true);

    partial void OnMessageNotificationsChanged(bool value)
    {
        Preferences.Default.Set("notifyMessages", value);
        AppLog.Write("UI", $"message notifications = {value}");
        if (!value) messageAlert.ClearAll();
    }

    void NotifyIncoming(Conversation chat, ChatMessage message)
    {
        if (!MessageNotifications || message.IsMine) return;
        // With the app lock on, the lock screen must not leak who wrote or what: show a plain notice.
        if (AppLock.Enabled)
        {
            messageAlert.Show("locked", "BtChat", Loc.Instance["newMessage"]);
            return;
        }
        var text = message.IsFile ? "📎 " + message.Display : message.Text;
        if (text.Length > 200) text = text[..200] + "…";
        messageAlert.Show(chat.Id, chat.Name, text);
    }

    // Encryption of messages and files: off by default; both devices have to agree when connecting.
    [ObservableProperty] bool encryptionEnabled = Preferences.Default.Get("encrypt", false);
    // True while the current connection is encrypted.
    [ObservableProperty] bool isEncrypted;
    // True when the other device's identity key was checked (matched the QR code or a key saved earlier).
    [ObservableProperty] bool isVerified;
    // Check the other device's identity: QR fingerprint and a warning when a known device's key changes. Off by default.
    [ObservableProperty] bool verifyPeers = Preferences.Default.Get("verifyPeers", false);

    public string SecurityBadge => !IsEncrypted ? "" : IsVerified ? "🔒✓" : "🔒";
    public string LocalFingerprintText => DeviceIdentity.LocalFingerprint;

    partial void OnIsEncryptedChanged(bool value) => OnPropertyChanged(nameof(SecurityBadge));
    partial void OnIsVerifiedChanged(bool value) => OnPropertyChanged(nameof(SecurityBadge));

    partial void OnVerifyPeersChanged(bool value)
    {
        Preferences.Default.Set("verifyPeers", value);
        AppLog.Write("UI", $"verify peers = {value}");
    }

    partial void OnEncryptionEnabledChanged(bool value)
    {
        Preferences.Default.Set("encrypt", value);
        AppLog.Write("UI", $"encryption = {value}");
        // A connection that already runs keeps the mode it started with.
        NotifySecurityHint();
    }

    void NotifySecurityHint() => OnPropertyChanged(nameof(EncryptionApplyHint));

    public string EncryptionApplyHint => IsConnected ? Loc.Instance["encryptHintNext"] : "";

    [ObservableProperty] bool concurrentFiles = Preferences.Default.Get("concurrentFiles", true);

    partial void OnConcurrentFilesChanged(bool value)
    {
        Preferences.Default.Set("concurrentFiles", value);
        AppLog.Write("UI", $"send several files at once = {value}");
        PumpSends();
    }

    public int TransferCount => Conversations.Sum(c => c.Messages.Count(m => m.ShowProgress));
    public bool HasTransfers => TransferCount > 0;
    public string TransferText => string.Format(Loc.Instance["transferCount"], TransferCount);

    void NotifyTransfers()
    {
        OnPropertyChanged(nameof(TransferCount));
        OnPropertyChanged(nameof(HasTransfers));
        OnPropertyChanged(nameof(TransferText));
    }

    // Bluetooth always sends one file after another; Wi-Fi can send several at once.
    int SendLimit => IsLanMode && ConcurrentFiles ? MaxParallelSends : 1;

    IReadOnlyList<string> addresses = Array.Empty<string>();
    public string LocalAddresses => addresses.Count > 0 ? string.Join("  |  ", addresses) : Loc.Instance["noNetwork"];
    public bool HasMessages => Messages.Count > 0;
    public bool IsLinked => IsConnected || autoTarget != null;
    public bool IsNotLinked => !IsLinked;

    partial void OnModeIndexChanged(int value)
    {
        if (value < 0) { ModeIndex = 0; return; }
        AppLog.Write("UI", $"mode changed to {value}");
        Preferences.Default.Set("mode", value);
        if (IsLinked) Disconnect();
        discovery.SetActive(value == 1);
        if (value == 0 && !btStarted) _ = EnsureBluetoothAsync(true);
        if (value == 1)
        {
            UpdateAddresses("switched to Wi-Fi mode");
            _ = SearchDevicesAsync();
        }
        else
        {
            FoundDevices.Clear();
            searched = false;
            NotifySearch();
        }
    }

    void UpdateAddresses(string reason)
    {
        try
        {
            tcp.LogNetworkState(reason);
            AppLog.Write("TCP", $"connectivity access={Connectivity.Current.NetworkAccess} profiles=[{string.Join(", ", Connectivity.Current.ConnectionProfiles)}]");
        }
        catch (Exception ex)
        {
            AppLog.Error("TCP", "network state log failed", ex);
        }
        addresses = tcp.GetLocalAddresses();
        OnPropertyChanged(nameof(LocalAddresses));
    }

    [RelayCommand]
    void RefreshAddresses()
    {
        AppLog.Write("UI", "refresh addresses pressed");
        UpdateAddresses("manual refresh");
    }

    partial void OnIsConnectedChanged(bool value)
    {
        RaiseLinked();
        NotifyComposer();
    }

    void NotifyComposer()
    {
        OnPropertyChanged(nameof(LinkedTitle));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(DraftPlaceholder));
    }

    partial void OnCurrentChatChanged(Conversation? oldValue, Conversation? newValue)
    {
        if (oldValue != null) oldValue.IsCurrent = false;
        if (newValue != null)
        {
            newValue.IsCurrent = true;
            newValue.Unread = 0;
        }
        Preferences.Default.Set("lastChat", newValue?.Id ?? "");
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentInitial));
        NotifyPins();
        ReplyTarget = null;
        NotifyComposer();
        ScrollRequested?.Invoke();
    }

    partial void OnMyNameChanged(string value)
    {
        LocalDevice.Name = value;
        helloCts?.Cancel();
        var cts = helloCts = new CancellationTokenSource();
        _ = ResendHelloAsync(cts.Token);
    }

    async Task ResendHelloAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(800, ct);
            var s = session;
            if (s != null) await s.SendHelloAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "resend hello failed", ex);
        }
    }

    void WatchChat(Conversation chat)
    {
        chat.Messages.CollectionChanged += (_, _) =>
        {
            if (ReferenceEquals(chat, CurrentChat))
            {
                OnPropertyChanged(nameof(HasMessages));
                NotifyPins();
                if (ReplyTarget != null && CurrentChat != null && !CurrentChat.Messages.Contains(ReplyTarget)) ReplyTarget = null;
                ScrollRequested?.Invoke();
            }
            NotifyTransfers();
        };
        chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Conversation.Name) && ReferenceEquals(chat, CurrentChat))
            {
                OnPropertyChanged(nameof(CurrentTitle));
                OnPropertyChanged(nameof(CurrentInitial));
            }
        };
    }

    Conversation GetOrCreateChat(string id, string name)
    {
        var chat = Conversations.FirstOrDefault(c => c.Id == id);
        if (chat == null)
        {
            chat = new Conversation(id, name);
            WatchChat(chat);
            Conversations.Insert(linkedChat != null ? Math.Min(1, Conversations.Count) : 0, chat);
        }
        else if (chat.PeerName != name)
        {
            chat.PeerName = name;
        }
        return chat;
    }

    string FolderFor(Conversation chat)
    {
        var folder = LocalDevice.SafeFolder(chat.Name);
        var clash = Conversations.Any(c => !ReferenceEquals(c, chat)
            && string.Equals(LocalDevice.SafeFolder(c.Name), folder, StringComparison.OrdinalIgnoreCase));
        if (!clash) return folder;
        var tag = chat.Id.Length > 4 ? chat.Id[..4] : chat.Id;
        return folder + " (" + tag + ")";
    }

    Conversation TargetChat()
    {
        var chat = linkedChat ?? CurrentChat;
        if (chat != null) return chat;
        chat = GetOrCreateChat(UnknownId, Loc.Instance["unknownDevice"]);
        CurrentChat = chat;
        return chat;
    }

    Conversation? ChatOf(ChatMessage message) => Conversations.FirstOrDefault(c => c.Messages.Contains(message));

    void RaiseLinked()
    {
        OnPropertyChanged(nameof(IsLinked));
        OnPropertyChanged(nameof(IsNotLinked));
        OnPropertyChanged(nameof(LinkedTitle));
        OnPropertyChanged(nameof(ConnectionDot));
        UpdateKeepAlive();
    }

    // The background service stays up while the chat is linked or while the proxy is sharing.
    void UpdateKeepAlive()
    {
        var text = IsLinked
            ? Loc.Instance[IsConnected ? "notifConnected" : "notifRetrying"]
            : proxy.NotificationText;
        keepAlive.Update(IsLinked || proxy.IsActive, text);
    }

    public ProxyViewModel Proxy => proxy;

    [RelayCommand]
    async Task OpenProxyAsync()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        await SafeAsync("open proxy", () => page.Navigation.PushModalAsync(new ProxyPage(proxy)));
    }

    public MainViewModel(IBluetoothTransport transport, TcpTransport tcp, IReceivedFileStore files, IQrScanner qr, DiscoveryService discovery, IKeepAlive keepAlive, IFileSource fileSource, IPermissionGate permissions, IVoiceRecorder recorder, IAudioPlayer audioPlayer, ICallAudio callAudioEngine, ICallAlert callAlertDevice, ICallVideo callVideoEngine, ProxyViewModel proxy, IMessageAlert messageAlertDevice)
    {
        messageAlert = messageAlertDevice;
        this.proxy = proxy;
        proxy.RunningChanged += UpdateKeepAlive;
        InitCalls(callAudioEngine, callAlertDevice, callVideoEngine);
        this.recorder = recorder;
        InitAudio(audioPlayer);
        this.keepAlive = keepAlive;
        ApplyTheme();
        this.permissions = permissions;
        this.fileSource = fileSource;
        keepAlive.ExitRequested += () => MainThread.BeginInvokeOnMainThread(() =>
        {
            AppLog.Write("APP", "exit requested (removed from recent apps), disconnecting");
            Disconnect();
            proxy.StopAll();
        });
        this.qr = qr;
        this.discovery = discovery;
        discovery.CanRespond = () => IsLanMode && session == null;
        discovery.DeviceFound += OnDeviceFound;
        FoundDevices.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFound));
            NotifySearch();
        };
        this.transport = transport;
        this.files = files;
        ChatMessage.Opener = files.OpenReadAsync;
        ChatMessage.VideoThumbOpener = files.GetVideoThumbnailAsync;
        this.tcp = tcp;
        ShareInbox.Arrived += () =>
        {
            if (started) _ = ProcessSharedAsync();
        };
        Conversations.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasChats));
            OnPropertyChanged(nameof(HasNoChats));
        };
        foreach (var chat in ChatHistory.Load())
        {
            WatchChat(chat);
            Conversations.Add(chat);
            // Received files that stopped half-way can still be continued, also after an app restart.
            foreach (var m in chat.Messages)
            {
                if (m.IsMine || !m.IsFile || !m.Failed || m.TransferKey == Guid.Empty) continue;
                resumes.Set(new FailedReceive { Key = m.TransferKey, Message = m, Expected = m.SizeBytes, Location = m.Location });
            }
        }
        var lastId = Preferences.Default.Get("lastChat", "");
        CurrentChat = Conversations.FirstOrDefault(c => c.Id == lastId) ?? Conversations.FirstOrDefault();
        IsDrawerOpen = Conversations.Count == 0;
        AppLog.Changed += () =>
        {
            if (!ShowLog || Interlocked.Exchange(ref logRefreshPending, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                Interlocked.Exchange(ref logRefreshPending, 0);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (ShowLog) LogText = AppLog.GetText(400);
                });
            });
        };
        SetStatus("idle");
        try
        {
            Connectivity.Current.ConnectivityChanged += (_, e) =>
            {
                AppLog.Write("TCP", $"connectivity changed: access={e.NetworkAccess} profiles=[{string.Join(", ", e.ConnectionProfiles)}]");
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    UpdateAddresses("connectivity changed");
                    if (IsLanMode && session == null) _ = SearchDevicesAsync();
                });
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("TCP", "cannot watch connectivity", ex);
        }
    }

    partial void OnShowLogChanged(bool value)
    {
        if (value) LogText = AppLog.GetText(400);
    }

    void SetStatus(string key)
    {
        statusKey = key;
        Status = Loc.Instance[key];
        RaiseLinked();
        AppLog.Write("UI", $"status={key}");
    }

    public async Task InitAsync()
    {
        if (started) return;
        started = true;
        // First start: the language is chosen on the first slide of the guide; wait for it before asking for permissions.
        await GuidePage.WaitForFirstRunAsync();
        await AskFirstLanguageAsync();
        CrashReport.ReportPrevious();
        AppLog.Write("APP", $"start {DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString} {DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}");
        UpdateAddresses("startup");
        _ = Task.Run(() => AcceptLoopAsync("tcp", tcp.AcceptAsync));
        _ = Task.Run(discovery.RunAsync);
        if (IsLanMode)
        {
            discovery.SetActive(true);
            _ = SearchDevicesAsync();
        }
        var firstRun = !Preferences.Default.Get("btPrompted", false);
        var interactive = IsBluetoothMode && firstRun;
        if (interactive) Preferences.Default.Set("btPrompted", true);
        await EnsureBluetoothAsync(interactive);
        await ProcessSharedAsync();
    }

    async Task AskFirstLanguageAsync()
    {
        if (Loc.HasSavedLanguage) return;
        try
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page != null)
            {
                var english = await page.DisplayAlert("Language / زبان", "Choose the app language\nزبان برنامه را انتخاب کنید", "English", "فارسی");
                if (Loc.Instance.IsFa == english) ToggleLanguage();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "language dialog failed", ex);
        }
        Loc.Instance.SaveChoice();
    }

    static async Task<bool> ConfirmAsync(string titleKey, string messageKey, string acceptKey)
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return false;
        var loc = Loc.Instance;
        return await page.DisplayAlert(loc[titleKey], loc[messageKey], loc[acceptKey], loc["cancel"]);
    }

    async Task<bool> EnsureBluetoothAsync(bool interactive)
    {
        var state = await transport.GetStateAsync();
        if (state == BtState.NoPermission && interactive)
        {
            if (await permissions.EnsureAsync(PermissionKind.Bluetooth)) state = await transport.GetStateAsync();
        }
        if (state == BtState.Off && interactive)
        {
            if (await ConfirmAsync("btTurnOnTitle", "btTurnOnAsk", "btTurnOn") && await transport.EnableAsync())
                state = await transport.GetStateAsync();
        }
        if (state != BtState.Ready)
        {
            AppLog.Write("APP", $"bluetooth not usable: {state}");
            if (IsBluetoothMode && !IsLinked) SetStatus(state == BtState.NoPermission ? "btNoPermission" : "btoff");
            return false;
        }
        if (statusKey is "btoff" or "btNoPermission") SetStatus("idle");
        if (btStarted) return true;
        btStarted = true;
        await LoadDevicesAsync();
        var savedId = Preferences.Default.Get("autoBt", "");
        AppLog.Write("APP", $"saved auto target id='{savedId}'");
        if (savedId.Length > 0)
        {
            var saved = Devices.FirstOrDefault(d => d.Id == savedId);
            if (saved != null)
            {
                SelectedDevice = saved;
                // In Wi-Fi mode the Bluetooth device is only pre-selected, not auto-connected.
                if (IsBluetoothMode) autoTarget = saved;
            }
            else
            {
                AppLog.Write("APP", "saved auto target not found in paired devices");
            }
        }
        _ = Task.Run(() => AcceptLoopAsync("bt", transport.AcceptAsync));
        _ = Task.Run(ReconnectLoopAsync);
        return true;
    }

    async Task AskBackgroundPermissionsAsync()
    {
        await files.EnsureReadyAsync();
        if (Preferences.Default.Get("notifAsked", false)) return;
        Preferences.Default.Set("notifAsked", true);
        await keepAlive.EnsurePermissionAsync();
    }

    async Task AcceptLoopAsync(string label, Func<CancellationToken, Task<Stream>> accept)
    {
        while (true)
        {
            try
            {
                var stream = await accept(CancellationToken.None);
                AppLog.Write("VM", $"accept loop {label} got inbound stream");
                await RunSessionAsync(stream, true, label + "-in");
            }
            catch (Exception ex)
            {
                AppLog.Error("VM", $"accept loop {label} error, retry in 1.5s", ex);
                await Task.Delay(1500);
            }
        }
    }

    async Task ReconnectLoopAsync()
    {
        var delay = 3000;
        while (true)
        {
            await Task.Delay(delay + Random.Shared.Next(0, 2000));
            var target = autoTarget;
            if (target == null || session != null || negotiating)
            {
                delay = 3000;
                continue;
            }
            AppLog.Write("VM", $"reconnect attempt to {target.Name}, delay was {delay}ms");
            SetStatus("retrying");
            try
            {
                await TryConnectBtAsync(target);
                delay = 3000;
            }
            catch (Exception ex)
            {
                delay = Math.Min(delay * 2, 20000);
                AppLog.Error("VM", $"reconnect failed, next delay {delay}ms", ex);
            }
        }
    }

    async Task TryConnectBtAsync(BtDevice target)
    {
        if (!await connectLock.WaitAsync(0))
        {
            AppLog.Write("VM", "connect skipped, another connect in progress");
            return;
        }
        try
        {
            if (session != null || negotiating)
            {
                AppLog.Write("VM", "connect skipped, session already exists");
                return;
            }
            var stream = await transport.ConnectAsync(target, CancellationToken.None);
            _ = RunSessionAsync(stream, false, "bt-out");
        }
        finally
        {
            connectLock.Release();
        }
    }

    async Task RunSessionAsync(Stream rawStream, bool inbound, string name, string? expectedFingerprint = null)
    {
        lock (gate)
        {
            if (session != null || negotiating)
            {
                AppLog.Write("VM", $"DROPPING {name} stream because a session already exists");
                rawStream.Dispose();
                return;
            }
            negotiating = true;
        }
        Stream stream;
        bool encrypted;
        var peerVerified = false;
        try
        {
            SetStatus("securing");
            var result = await SecureLink.NegotiateAsync(rawStream, inbound, EncryptionEnabled, name, MakeSecurityPrompts());
            if (result.Cancelled)
            {
                AppLog.Write("VM", $"{name} not started: {result.FailKey}");
                if (result.CancelledLocally && !inbound)
                {
                    autoTarget = null;
                    Preferences.Default.Remove("autoBt");
                }
                lock (gate) negotiating = false;
                SetStatus(result.FailKey ?? "secFailed");
                return;
            }
            var (failKey, verified) = await VerifyPeerAsync(result, expectedFingerprint);
            if (failKey != null)
            {
                AppLog.Write("VM", $"{name} not started: {failKey}");
                result.Stream.Dispose();
                lock (gate) negotiating = false;
                SetStatus(failKey);
                return;
            }
            stream = result.Stream;
            encrypted = result.Encrypted;
            peerVerified = verified;
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", $"{name} security setup failed", ex);
            rawStream.Dispose();
            lock (gate) negotiating = false;
            SetStatus("secFailed");
            return;
        }
        ChatSession current;
        lock (gate)
        {
            negotiating = false;
            current = new ChatSession(stream, files, name, resumes);
            session = current;
        }
        IsEncrypted = encrypted;
        IsVerified = encrypted && peerVerified;
        NotifySecurityHint();
        AppLog.Write("VM", $"session started {name} inbound={inbound} encrypted={encrypted} verified={IsVerified}");
        if (inbound)
        {
            autoTarget = null;
            Preferences.Default.Remove("autoBt");
        }
        Conversation? chat = null;
        current.PeerIdentified += () => MainThread.BeginInvokeOnMainThread(() =>
        {
            var first = chat == null;
            var found = GetOrCreateChat(current.PeerId!, current.PeerName!);
            chat = found;
            linkedChat = found;
            found.IsLinked = true;
            // The connected device is always the first one in the list.
            var foundIndex = Conversations.IndexOf(found);
            if (foundIndex > 0) Conversations.Move(foundIndex, 0);
            current.ReceiveFolder = FolderFor(found);
            if (first)
            {
                CurrentChat = found;
                IsDrawerOpen = false;
            }
            NotifyComposer();
            SaveHistory();
        });
        current.RetryRequested += key => MainThread.BeginInvokeOnMainThread(() => _ = HandleRetryRequestAsync(current, key));
        HookCall(current);
        current.TextEdited += (id, text) => MainThread.BeginInvokeOnMainThread(() => ApplyReceivedEdit(id, text));
        current.MessageReceived += m => MainThread.BeginInvokeOnMainThread(() =>
            AddMessage(chat ?? GetOrCreateChat(UnknownId, Loc.Instance["unknownDevice"]), m));
        IsConnected = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            FoundDevices.Clear();
            var shown = qrPage;
            if (shown == null) return;
            try { await shown.Navigation.PopModalAsync(); }
            catch (Exception ex) { AppLog.Error("QR", "closing qr page failed", ex); }
        });
        SetStatus("connected");
        _ = MainThread.InvokeOnMainThreadAsync(AskBackgroundPermissionsAsync);
        try
        {
            await current.RunAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", $"session {name} ended with error", ex);
        }
        finally
        {
            current.Dispose();
            lock (gate)
            {
                session = null;
            }
            FailQueuedSends();
            IsConnected = false;
            IsEncrypted = false;
            IsVerified = false;
            NotifySecurityHint();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (ReferenceEquals(callSession, current)) EndCallLocal();
                if (chat != null)
                {
                    chat.IsLinked = false;
                    if (ReferenceEquals(linkedChat, chat)) linkedChat = null;
                }
                NotifyComposer();
            });
            AppLog.Write("VM", $"session {name} finished, autoTarget={(autoTarget != null ? autoTarget.Name : "none")}");
            SetStatus(autoTarget != null ? "retrying" : "idle");
        }
    }

    // ---- Encryption questions (shown when the two devices have different encryption settings)

    readonly Dictionary<string, DateTime> securityDeclined = new();

    bool RecentlyDeclined(string peerId)
    {
        lock (securityDeclined)
            return securityDeclined.TryGetValue(peerId, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(2);
    }

    void RememberDeclined(string peerId)
    {
        lock (securityDeclined) securityDeclined[peerId] = DateTime.UtcNow;
    }

    SecurityPrompts MakeSecurityPrompts() => new()
    {
        LocalOff = async peerId =>
        {
            // The same device keeps retrying after a "no": do not ask again for two minutes.
            if (RecentlyDeclined(peerId)) return LocalChoice.Cancel;
            var loc = Loc.Instance;
            var picked = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page == null) return null;
                return await page.DisplayActionSheet(loc["secMismatchOff"], loc["cancel"], null, loc["secEnable"], loc["secContinuePlain"]);
            });
            if (picked == loc["secEnable"]) return LocalChoice.EnableEncryption;
            if (picked == loc["secContinuePlain"]) return LocalChoice.ContinuePlain;
            RememberDeclined(peerId);
            return LocalChoice.Cancel;
        },
        LocalOnConfirmPlain = async peerId =>
        {
            if (RecentlyDeclined(peerId)) return false;
            var loc = Loc.Instance;
            var agreed = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page == null) return false;
                return await page.DisplayAlert(loc["secTitle"], loc["secMismatchOn"], loc["secContinuePlain"], loc["cancel"]);
            });
            if (!agreed) RememberDeclined(peerId);
            return agreed;
        },
        EnableEncryption = () => MainThread.BeginInvokeOnMainThread(() => EncryptionEnabled = true)
    };

    // Checks who is on the other end (only when "verify devices" is on and the link is encrypted).
    // Returns a localization key when the connection must not start, and whether the identity was confirmed.
    async Task<(string? FailKey, bool Verified)> VerifyPeerAsync(SecurityResult result, string? expectedFingerprint)
    {
        if (!VerifyPeers) return (null, false);
        // The QR code of the other device carried its key fingerprint: it has to match exactly.
        if (expectedFingerprint != null)
        {
            if (!result.Encrypted) return ("secNotVerified", false);
            if (!PeerTrust.Same(result.PeerFingerprint, expectedFingerprint))
            {
                AppLog.Write("SEC", "peer key does not match the QR code");
                return ("secWrongDevice", false);
            }
            PeerTrust.Set(result.PeerId, result.PeerFingerprint!);
            return (null, true);
        }
        if (!result.Encrypted || result.PeerFingerprint == null) return (null, false);
        var known = PeerTrust.Get(result.PeerId);
        if (known == null)
        {
            // First time we see this device: remember its key (trust on first use).
            PeerTrust.Set(result.PeerId, result.PeerFingerprint);
            return (null, false);
        }
        if (PeerTrust.Same(known, result.PeerFingerprint)) return (null, true);

        AppLog.Write("SEC", "peer key changed since the last connection");
        if (RecentlyDeclined(result.PeerId)) return ("secKeyRejected", false);
        var loc = Loc.Instance;
        var chat = Conversations.FirstOrDefault(c => c.Id == result.PeerId);
        var who = chat?.Name ?? loc["unknownDevice"];
        var trust = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null) return false;
            return await page.DisplayAlert(loc["secKeyChangedTitle"], string.Format(loc["secKeyChanged"], who), loc["secTrust"], loc["cancel"]);
        });
        if (!trust)
        {
            RememberDeclined(result.PeerId);
            return ("secKeyRejected", false);
        }
        PeerTrust.Set(result.PeerId, result.PeerFingerprint);
        return (null, false);
    }

    async Task LoadDevicesAsync()
    {
        var list = await transport.GetPairedDevicesAsync();
        Devices.Clear();
        foreach (var d in list) Devices.Add(d);
    }

    [RelayCommand]
    async Task RefreshAsync()
    {
        var wasStarted = btStarted;
        if (!await EnsureBluetoothAsync(true)) return;
        if (wasStarted) await LoadDevicesAsync();
    }

    [RelayCommand]
    async Task ConnectAsync()
    {
        AppLog.Write("UI", $"Connect pressed sessionExists={session != null}");
        if (session != null) return;
        if (!await EnsureBluetoothAsync(true)) return;
        var target = SelectedDevice;
        if (target == null) return;
        autoTarget = target;
        Preferences.Default.Set("autoBt", target.Id);
        SetStatus("connecting");
        try
        {
            await TryConnectBtAsync(target);
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "manual connect failed", ex);
            SetStatus("retrying");
        }
    }

    // Accepts Persian/Arabic digits and separators that people type by mistake.
    static string NormalizeAddress(string raw)
    {
        var sb = new StringBuilder();
        foreach (var c in raw.Trim())
        {
            if (c >= '\u06F0' && c <= '\u06F9') sb.Append((char)('0' + (c - '\u06F0')));
            else if (c >= '\u0660' && c <= '\u0669') sb.Append((char)('0' + (c - '\u0660')));
            else if (c == '\u066B' || c == '\u060C' || c == ',' || c == '\u3002') sb.Append('.');
            else if (!char.IsWhiteSpace(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    [RelayCommand]
    async Task ConnectLanAsync()
    {
        var address = NormalizeAddress(Host);
        AppLog.Write("UI", $"Connect via IP pressed host='{Host}' normalized='{address}' sessionExists={session != null}");
        if (address.Length == 0 || session != null) return;
        if (address.Count(c => c == '.') != 3 || !IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            AppLog.Write("TCP", $"rejected invalid address '{address}'");
            SetStatus("badIp");
            return;
        }
        if (tcp.IsLocalAddress(ip))
        {
            AppLog.Write("TCP", $"rejected own address {address}");
            SetStatus("selfIp");
            return;
        }
        await ConnectViaTcpAsync(new[] { address });
    }

    // Tries each address in order; the first one that answers wins.
    async Task ConnectViaTcpAsync(IReadOnlyList<string> candidates, int timeoutSeconds = 8, string? expectedFingerprint = null)
    {
        if (!await connectLock.WaitAsync(0))
        {
            AppLog.Write("TCP", "connect skipped, another connect in progress");
            return;
        }
        try
        {
            tcp.LogNetworkState("before connect");
            SetStatus("connecting");
            Exception? last = null;
            foreach (var address in candidates)
            {
                try
                {
                    Host = address;
                    Preferences.Default.Set("host", address);
                    var stream = await tcp.ConnectAsync(address, CancellationToken.None, timeoutSeconds);
                    _ = RunSessionAsync(stream, false, "tcp-out", expectedFingerprint);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            switch (last)
            {
                case OperationCanceledException:
                    SetStatus("tcpTimeout");
                    break;
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    SetStatus("tcpRefused");
                    break;
                default:
                    SetStatus("failed");
                    break;
            }
        }
        finally
        {
            connectLock.Release();
        }
    }

    [RelayCommand]
    async Task SearchDevicesAsync()
    {
        if (IsSearching || !IsLanMode || session != null) return;
        IsSearching = true;
        searched = true;
        FoundDevices.Clear();
        AppLog.Write("UI", "search devices started");
        try
        {
            tcp.LogNetworkState("before search");
            await discovery.SearchAsync(TimeSpan.FromSeconds(3.5));
        }
        catch (Exception ex)
        {
            AppLog.Error("DISCOVERY", "search failed", ex);
        }
        finally
        {
            IsSearching = false;
            AppLog.Write("UI", $"search devices done, found={FoundDevices.Count}");
        }
    }

    void OnDeviceFound(FoundDevice device) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (!IsSearching || FoundDevices.Any(d => d.Address == device.Address)) return;
        FoundDevices.Add(device);
    });

    [RelayCommand]
    async Task ConnectFoundAsync(FoundDevice? device)
    {
        if (device == null || session != null) return;
        AppLog.Write("UI", $"connect to found device {device.Name} {device.Address}");
        await ConnectViaTcpAsync(new[] { device.Address });
    }

    [RelayCommand]
    async Task ShowQrAsync()
    {
        UpdateAddresses("show qr");
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        var loc = Loc.Instance;
        if (addresses.Count == 0)
        {
            await page.DisplayAlert(loc["showQr"], loc["noNetwork"], "OK");
            return;
        }
        // The fingerprint goes into the QR code only when both encryption and device verification are on.
        var fingerprint = EncryptionEnabled && VerifyPeers ? PeerTrust.Compact(DeviceIdentity.LocalFingerprint) : null;
        var payload = QrPayload.Build(addresses, DiscoveryService.DeviceName(), fingerprint);
        AppLog.Write("QR", $"showing qr, payload={payload}");
        try
        {
            var shown = new QrShowPage(payload, LocalAddresses);
            qrPage = shown;
            shown.Disappearing += (_, _) =>
            {
                if (qrPage == shown) qrPage = null;
            };
            await page.Navigation.PushModalAsync(shown);
        }
        catch (Exception ex)
        {
            AppLog.Error("QR", "show qr failed", ex);
            qrPage = null;
        }
    }

    [RelayCommand]
    async Task ScanQrAsync()
    {
        if (session != null || !qr.IsSupported) return;
        AppLog.Write("QR", "scan pressed");
        string? text;
        try
        {
            text = await qr.ScanAsync();
        }
        catch (PermissionException ex)
        {
            AppLog.Error("QR", "camera permission denied", ex);
            SetStatus("qrNoCamera");
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("QR", "scan failed", ex);
            SetStatus("qrFailed");
            return;
        }
        if (text == null)
        {
            AppLog.Write("QR", "scan closed without result");
            return;
        }
        AppLog.Write("QR", $"scanned: {text}");
        if (!QrPayload.TryParse(text, out var info))
        {
            SetStatus("qrInvalid");
            return;
        }
        var candidates = tcp.OrderCandidates(info.Addresses);
        AppLog.Write("QR", $"peer '{info.Name}' addresses=[{string.Join(", ", info.Addresses)}] try order=[{string.Join(", ", candidates)}]");
        if (candidates.Count == 0)
        {
            SetStatus("selfIp");
            return;
        }
        await ConnectViaTcpAsync(candidates, 4, VerifyPeers ? info.Fingerprint : null);
    }

    [RelayCommand]
    void Disconnect()
    {
        AppLog.Write("UI", "Disconnect pressed");
        autoTarget = null;
        Preferences.Default.Remove("autoBt");
        session?.Dispose();
        if (session == null) SetStatus("idle");
    }

    [RelayCommand]
    async Task SendAsync()
    {
        var s = session;
        // Windows text boxes use \r (or \r\n) for line breaks; keep one kind so every device shows the same lines.
        var text = Draft.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (s == null || text.Length == 0 || !CanSend) return;
        Draft = "";
        try
        {
            var target = TargetChat();
            var replying = ReplyTarget;
            ReplyTarget = null;
            var first = true;
            // One frame is at most 1 MB, so a huge paste goes out as several messages (cut at line breaks).
            foreach (var part in SplitLongText(text))
            {
                var messageId = Guid.NewGuid();
                if (first && replying != null)
                {
                    var replyId = ReplyKeyOf(replying);
                    var replySender = ReplySenderOf(replying);
                    var replyPreview = ReplyPreviewOf(replying);
                    await s.SendTextReplyAsync(messageId, part, replyId, replySender, replyPreview);
                    AddMessage(target, new ChatMessage
                    {
                        MessageId = messageId, Text = part, IsMine = true, SenderName = LocalDevice.Name,
                        ReplyToId = replyId, ReplySender = replySender, ReplyPreview = replyPreview
                    });
                }
                else
                {
                    await s.SendTextAsync(messageId, part);
                    AddMessage(target, new ChatMessage { MessageId = messageId, Text = part, IsMine = true, SenderName = LocalDevice.Name });
                }
                first = false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "send text failed", ex);
            SetStatus("failed");
        }
    }

    const int MaxMessageChars = 100_000;

    static IEnumerable<string> SplitLongText(string text)
    {
        while (text.Length > MaxMessageChars)
        {
            var cut = text.LastIndexOf('\n', MaxMessageChars - 1, MaxMessageChars / 2);
            if (cut <= 0) cut = MaxMessageChars;
            yield return text[..cut].TrimEnd();
            text = text[cut..].TrimStart('\n');
        }
        if (text.Length > 0) yield return text;
    }

    sealed class PendingSend
    {
        public required ChatSession Session { get; init; }
        public required uint Id { get; init; }
        public required PickedFile File { get; init; }
        public required ChatMessage Message { get; init; }
        public required long Size { get; init; }
        public required Guid Key { get; init; }
        public CancellationTokenSource Cts { get; } = new();
    }

    [RelayCommand]
    async Task AttachAsync()
    {
        var s = session;
        if (s == null || !CanSend) return;
        var chat = TargetChat();
        IReadOnlyList<PickedFile> picked;
        try
        {
            picked = await fileSource.PickAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "file picker failed", ex);
            return;
        }
        if (picked.Count == 0) return;
        AppLog.Write("UI", $"files picked: {picked.Count}");
        await EnqueueFilesAsync(s, chat, picked);
    }

    // Puts files into the chat and the send queue, and announces each one to the other device.
    async Task EnqueueFilesAsync(ChatSession s, Conversation chat, IReadOnlyList<PickedFile> picked)
    {
        foreach (var file in picked)
        {
            var size = file.Size;
            var item = new PendingSend
            {
                Session = s,
                Id = s.NewFileId(),
                File = file,
                Size = size,
                Key = Guid.NewGuid(),
                Message = new ChatMessage { Text = file.Name, IsMine = true, IsFile = true, Location = file.Location, SenderName = LocalDevice.Name }
            };
            item.Message.TransferKey = item.Key;
            item.Message.Cts = item.Cts;
            item.Message.SetQueued();
            AddMessage(chat, item.Message);
            lock (sendLock) sendQueue.Add(item);
            try
            {
                await s.OfferFileAsync(item.Id, file.Name, size, item.Key);
            }
            catch (Exception ex)
            {
                AppLog.Error("VM", "offer file failed", ex);
                lock (sendLock) sendQueue.Remove(item);
                item.Message.Fail();
                SetStatus("failed");
            }
        }
        PumpSends();
    }

    // The chosen device is not connected: the files appear in its chat as "not sent yet" with a Retry button,
    // which works as soon as the connection is back. Each file is copied into the app first (see OutgoingStore).
    async Task QueueOfflineAsync(Conversation chat, IReadOnlyList<SharedFile> items)
    {
        foreach (var item in items)
        {
            var message = new ChatMessage { Text = item.Name, IsMine = true, IsFile = true, SenderName = LocalDevice.Name, TransferKey = Guid.NewGuid() };
            message.SetOffered(item.Size);
            message.SetQueued("shareCopying");
            AddMessage(chat, message);
            try
            {
                var path = await Task.Run(() => OutgoingStore.CopyAsync(item.Name, item.Location, fileSource.OpenAsync, CancellationToken.None));
                message.Location = path;
                message.Fail("fileNotSent");
                AppLog.Write("VM", $"shared file waits for a connection: {item.Name}");
            }
            catch (Exception ex)
            {
                AppLog.Error("VM", $"copying shared file failed: {item.Name}", ex);
                message.Fail();
            }
        }
        SaveHistory();
    }

    // ---- files shared from other apps -----------------------------------------------------------

    bool sharing;

    // Shows the "send to" list for files other apps shared with us, then sends them to the chosen device.
    async Task ProcessSharedAsync()
    {
        if (sharing) return;
        sharing = true;
        try
        {
            while (true)
            {
                var items = ShareInbox.Take();
                if (items.Count == 0) return;
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page == null)
                {
                    ShareInbox.Return(items);
                    return;
                }
                AppLog.Write("UI", $"shared files to deliver: {items.Count}");
                // The connected device comes first (the sort is stable, so the rest stays in recent-first order).
                var chooser = new ShareTargetPage(items, Conversations.OrderByDescending(c => c.IsLinked).ToList());
                await page.Navigation.PushModalAsync(chooser);
                var chat = await chooser.Result;
                if (chat == null)
                {
                    AppLog.Write("UI", "share canceled");
                    continue;
                }
                await SendSharedAsync(chat, items);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "handling shared files failed", ex);
        }
        finally
        {
            sharing = false;
        }
    }

    async Task SendSharedAsync(Conversation chat, IReadOnlyList<SharedFile> items)
    {
        var s = session;
        CurrentChat = chat;
        if (!IsWide) IsDrawerOpen = false;
        if (s == null || !IsConnected || !ReferenceEquals(chat, linkedChat))
        {
            await QueueOfflineAsync(chat, items);
            return;
        }
        var picked = items.Select(i => new PickedFile(i.Name, i.Location, i.Size, () => fileSource.OpenAsync(i.Location))).ToList();
        await EnqueueFilesAsync(s, chat, picked);
    }

    // Starts queued files while there is a free slot: 1 slot for Bluetooth, up to 4 for Wi-Fi "at once".
    void PumpSends()
    {
        while (true)
        {
            PendingSend? next;
            lock (sendLock)
            {
                if (activeSends >= SendLimit || sendQueue.Count == 0) return;
                next = sendQueue[0];
                sendQueue.RemoveAt(0);
                activeSends++;
            }
            _ = RunSendAsync(next);
        }
    }

    async Task RunSendAsync(PendingSend item)
    {
        var message = item.Message;
        // Saved now, so a transfer that is cut off (even by the app being killed) can be continued later.
        MainThread.BeginInvokeOnMainThread(SaveHistory);
        try
        {
            await using var source = await item.File.Open();
            var size = item.Size;
            try { if (source.CanSeek) size = source.Length; } catch { }
            message.MarkStart(size);
            await Task.Run(() => item.Session.SendFileAsync(item.Id, source, (done, total) => message.Report(done, total), size, item.Cts.Token));
            message.Complete();
        }
        catch (OperationCanceledException) when (item.Cts.IsCancellationRequested)
        {
            AppLog.Write("VM", $"send file canceled by user: {message.Text}");
            message.Fail("fileCanceled");
            await TryCancelOnPeerAsync(item);
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", $"send file failed: {message.Text}", ex);
            message.Fail();
            SetStatus("failed");
            await TryCancelOnPeerAsync(item);
        }
        finally
        {
            message.Cts = null;
            lock (sendLock) activeSends--;
            PumpSends();
        }
    }

    static async Task TryCancelOnPeerAsync(PendingSend item)
    {
        try
        {
            await item.Session.CancelFileAsync(item.Id);
        }
        catch
        {
            // The connection is gone; the other side already dropped its partial file.
        }
    }

    void FailQueuedSends()
    {
        List<PendingSend> dropped;
        lock (sendLock)
        {
            dropped = sendQueue.ToList();
            sendQueue.Clear();
        }
        foreach (var item in dropped) item.Message.Fail();
    }

    [RelayCommand]
    async Task RetryFileAsync(ChatMessage? message)
    {
        if (message == null || !message.CanRetry) return;
        AppLog.Write("UI", $"retry pressed for {message.Text} mine={message.IsMine}");
        var s = session;
        var chat = ChatOf(message);
        // Only possible while connected to the same device the file was exchanged with.
        if (s == null || chat == null || !ReferenceEquals(chat, linkedChat))
        {
            message.ShowNote("retryNeedsConnection");
            return;
        }
        if (message.IsMine)
        {
            await StartRetryAsync(s, message);
            return;
        }
        try
        {
            message.ShowNote("retryRequested");
            await s.RequestRetryAsync(message.TransferKey);
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "retry request failed", ex);
            message.ShowNote("failed");
        }
    }

    // Sends a failed file again. The receiver continues from the bytes it already has (or starts over if it has none).
    async Task<bool> StartRetryAsync(ChatSession s, ChatMessage message, bool peerAsked = false)
    {
        var location = message.Location;
        if (location == null) return false;
        long size = message.SizeBytes > 0 ? message.SizeBytes : -1;
        try
        {
            await using var probe = await fileSource.OpenAsync(location);
            if (probe.CanSeek) size = probe.Length;
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", $"retry: cannot open {message.Text}", ex);
            message.ShowNote("fileMissing");
            return false;
        }
        // The button may have been pressed twice while the file was being opened.
        // peerAsked: the other device wants the file again even though here it looked finished.
        if ((!message.CanRetry && !peerAsked) || message.ShowProgress || !ReferenceEquals(s, session)) return false;
        if (message.TransferKey == Guid.Empty) message.TransferKey = Guid.NewGuid();
        var item = new PendingSend
        {
            Session = s,
            Id = s.NewFileId(),
            File = new PickedFile(message.Text, location, size, () => fileSource.OpenAsync(location)),
            Size = size,
            Key = message.TransferKey,
            Message = message
        };
        message.Revive();
        message.Cts = item.Cts;
        lock (sendLock) sendQueue.Add(item);
        try
        {
            await s.OfferFileAsync(item.Id, message.Text, size, item.Key);
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "retry: offer failed", ex);
            lock (sendLock) sendQueue.Remove(item);
            message.Fail();
            SetStatus("failed");
            return true;
        }
        PumpSends();
        return true;
    }

    // The other device asks for a file again (its retry button): find it and send it, or say it is not available.
    async Task HandleRetryRequestAsync(ChatSession from, Guid key)
    {
        var message = linkedChat?.Messages.FirstOrDefault(m => m.IsMine && m.IsFile && m.TransferKey == key);
        if (message != null && message.ShowProgress) return;
        var ok = message != null && message.Location != null && ReferenceEquals(from, session) && await StartRetryAsync(from, message, peerAsked: true);
        if (ok) return;
        try
        {
            await from.DenyRetryAsync(key);
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "deny retry failed", ex);
        }
    }

    [RelayCommand]
    void CancelFile(ChatMessage? message)
    {
        if (message == null || !message.CanCancel) return;
        AppLog.Write("UI", $"cancel sending {message.Text}");
        PendingSend? queued;
        lock (sendLock)
        {
            queued = sendQueue.FirstOrDefault(p => p.Message == message);
            if (queued != null) sendQueue.Remove(queued);
        }
        if (queued != null)
        {
            // Never started: just drop it and tell the other side to forget the offer.
            message.Fail("fileCanceled");
            _ = TryCancelOnPeerAsync(queued);
            return;
        }
        message.Cts?.Cancel();
    }

    void AddMessage(Conversation chat, ChatMessage message)
    {
        message.Finished += SaveHistory;
        message.Started += SaveHistory;
        message.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMessage.ShowProgress)) NotifyTransfers();
        };
        chat.Messages.Add(message);
        if (!message.IsMine && !ReferenceEquals(chat, CurrentChat)) chat.Unread++;
        if (!message.IsMine)
        {
            // A file is announced when it has arrived, a text right away.
            if (message.IsFile)
                message.Finished += () => { if (!message.Failed) NotifyIncoming(chat, message); };
            else
                NotifyIncoming(chat, message);
        }
        // Newest activity first, but the connected device stays on top.
        var top = linkedChat != null && !ReferenceEquals(chat, linkedChat) ? 1 : 0;
        var index = Conversations.IndexOf(chat);
        if (index > top) Conversations.Move(index, top);
        if (!message.ShowProgress) SaveHistory();
    }

    void SaveHistory() => ChatHistory.Save(Conversations);

    [RelayCommand]
    async Task ClearHistoryAsync()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        var chat = CurrentChat;
        if (chat == null) return;
        var loc = Loc.Instance;
        var ok = await page.DisplayAlert(loc["clearHistory"], loc["clearHistoryAsk"], loc["delete"], loc["cancel"]);
        if (!ok) return;
        foreach (var m in chat.Messages.Where(m => !m.ShowProgress).ToList())
        {
            chat.Messages.Remove(m);
            ForgetMessage(m);
        }
        SaveHistory();
        AppLog.Write("UI", "chat history cleared");
    }

    async Task DeleteMessageAsync(ChatMessage message, Page page)
    {
        var loc = Loc.Instance;
        if (message.IsReceivedFile && message.Location != null && !message.Failed)
        {
            var ok = await page.DisplayAlert(message.Text, loc["deleteFileAsk"], loc["delete"], loc["cancel"]);
            if (!ok) return;
            try
            {
                await files.DeleteAsync(message.Location);
            }
            catch (Exception ex)
            {
                AppLog.Error("VM", "delete file failed", ex);
                await page.DisplayAlert(message.Text, loc["deleteFileFailed"], "OK");
            }
        }
        ChatOf(message)?.Messages.Remove(message);
        ForgetMessage(message);
        SaveHistory();
    }

    // A message leaves the chat for good: free what belongs to it (saved access, half-received file, resume info).
    void ForgetMessage(ChatMessage message)
    {
        ReleaseIfUnused(message);
        if (message.IsMine && message.IsFile) OutgoingStore.Delete(message.Location);
        if (!message.IsReceivedFile) return;
        resumes.RemoveMessage(message);
        if (message.Failed && message.Location != null)
        {
            var partial = message.Location;
            _ = SafeAsync("delete partial file", () => files.DeleteAsync(partial));
        }
    }

    // A sent file keeps its saved access only while some chat message still points to it.
    void ReleaseIfUnused(ChatMessage message)
    {
        var location = message.Location;
        if (!message.IsMine || !message.IsFile || location == null) return;
        if (Conversations.Any(c => c.Messages.Any(m => m.Location == location))) return;
        fileSource.Release(location);
    }

    [RelayCommand]
    async Task OpenFileAsync(ChatMessage? message)
    {
        if (message?.Location == null || message.ShowProgress || message.Failed) return;
        if (message.ShowAudioPlayer) return;
        if (message.CanPreview)
        {
            await ShowMediaAsync(message);
            return;
        }
        await SafeAsync("open file", () => files.OpenAsync(message.Location, message.Text));
    }

    async Task ShowMediaAsync(ChatMessage message)
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        IReadOnlyList<ChatMessage>? gallery = null;
        if (message.IsImage)
            gallery = ChatOf(message)?.Messages.Where(m => m.CanPreview && m.IsImage).ToList();
        await SafeAsync("show media", () => page.Navigation.PushModalAsync(new MediaViewerPage(message, files, gallery, fileSource, ChatOf(message)?.Messages.ToList())));
    }

    [RelayCommand]
    async Task MessageMenuAsync(ChatMessage? message)
    {
        if (message == null) return;
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        var loc = Loc.Instance;
        if (message.IsFile)
        {
            if (!message.HasMenu) return;
            var options = new List<string>();
            var previewLabel = message.CanPreview ? loc[message.IsImage ? "viewImage" : "playMedia"] : null;
            var openLabel = message.IsMedia ? loc["openWith"] : loc["openFile"];
            if (!message.Failed && message.Location != null)
            {
                if (previewLabel != null) options.Add(previewLabel);
                options.Add(openLabel);
                if (message.IsReceivedFile) options.Add(loc["openFolder"]);
                options.Add(loc["share"]);
            }
            var replyLabel = loc["reply"];
            options.Add(replyLabel);
            var pinLabel = loc[message.IsPinned ? "unpin" : "pin"];
            options.Add(pinLabel);
            var deleteLabel = message.IsReceivedFile && !message.Failed ? loc["deleteFile"] : loc["deleteMessage"];
            options.Add(deleteLabel);
            var picked = await page.DisplayActionSheet(message.Text, loc["cancel"], null, options.ToArray());
            if (picked == null) return;
            if (picked == deleteLabel)
                await DeleteMessageAsync(message, page);
            else if (picked == replyLabel)
                StartReply(message);
            else if (picked == pinLabel)
                TogglePin(message);
            else if (picked == previewLabel)
                await ShowMediaAsync(message);
            else if (picked == openLabel)
                await SafeAsync("open file", () => files.OpenAsync(message.Location!, message.Text));
            else if (picked == loc["openFolder"])
                await SafeAsync("open folder", () => files.ShowInFolderAsync(message.Location!));
            else if (picked == loc["share"])
                await SafeAsync("share file", () => files.ShareAsync(message.Location!, message.Text));
            return;
        }
        var textPinLabel = loc[message.IsPinned ? "unpin" : "pin"];
        var textReplyLabel = loc["reply"];
        var textOptions = new List<string> { textReplyLabel, loc["copyText"], loc["share"], textPinLabel };
        var canEdit = message.IsMine && message.MessageId != Guid.Empty;
        if (canEdit) textOptions.Add(loc["edit"]);
        textOptions.Add(loc["deleteMessage"]);
        var choice = await page.DisplayActionSheet(null, loc["cancel"], null, textOptions.ToArray());
        if (canEdit && choice == loc["edit"])
            await EditMessageAsync(message, page);
        else if (choice == textReplyLabel)
            StartReply(message);
        else if (choice == textPinLabel)
            TogglePin(message);
        else if (choice == loc["copyText"])
            await Clipboard.Default.SetTextAsync(message.Text);
        else if (choice == loc["share"])
            await Share.Default.RequestAsync(new ShareTextRequest { Text = message.Text });
        else if (choice == loc["deleteMessage"])
        {
            ChatOf(message)?.Messages.Remove(message);
            SaveHistory();
        }
    }

    // ---- Reply to a message

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplyTarget))]
    [NotifyPropertyChangedFor(nameof(ReplyBarTitle))]
    [NotifyPropertyChangedFor(nameof(ReplyBarPreview))]
    ChatMessage? replyTarget;

    public bool HasReplyTarget => ReplyTarget != null;
    public string ReplyBarTitle => ReplyTarget == null ? "" : string.Format(Loc.Instance["replyTo"], ReplySenderOf(ReplyTarget));
    public string ReplyBarPreview => ReplyTarget == null ? "" : ReplyPreviewOf(ReplyTarget);

    // Text messages are found by their message id; files by their transfer key (the same on both devices).
    static Guid ReplyKeyOf(ChatMessage m) => m.MessageId != Guid.Empty ? m.MessageId : m.TransferKey;

    static string ReplySenderOf(ChatMessage m) => m.IsMine ? LocalDevice.Name : m.SenderName;

    static string ReplyPreviewOf(ChatMessage m)
    {
        var text = m.IsFile ? m.Display : m.Text;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return line.Trim();
    }

    void StartReply(ChatMessage message)
    {
        ReplyTarget = message;
        FocusDraftRequested?.Invoke();
    }

    [RelayCommand]
    void CancelReply() => ReplyTarget = null;

    // Tap on the quote inside a bubble: jump to the original message (if this device still has it).
    [RelayCommand]
    void ReplyJump(ChatMessage? message)
    {
        if (message == null || message.ReplyToId == Guid.Empty || CurrentChat == null) return;
        var original = CurrentChat.Messages.FirstOrDefault(m =>
            m.MessageId == message.ReplyToId || (m.IsFile && m.TransferKey == message.ReplyToId));
        if (original != null) ScrollToMessageRequested?.Invoke(original);
    }

    // ---- Pinned messages (local to this device, several per chat, shown in a bar above the messages)

    List<ChatMessage> CurrentPins() => CurrentChat == null
        ? new List<ChatMessage>()
        : CurrentChat.Messages.Where(m => m.IsPinned).OrderBy(m => m.PinnedAt).ToList();

    ChatMessage? ShownPin
    {
        get
        {
            var pins = CurrentPins();
            if (pins.Count == 0) return null;
            return pins[Math.Clamp(CurrentChat!.PinCursor, 0, pins.Count - 1)];
        }
    }

    public bool HasPins => CurrentPins().Count > 0;

    public string PinTitle
    {
        get
        {
            var pins = CurrentPins();
            var title = Loc.Instance["pinnedMessage"];
            if (pins.Count < 2) return title;
            var index = Math.Clamp(CurrentChat!.PinCursor, 0, pins.Count - 1);
            return $"{title} #{index + 1}";
        }
    }

    public string PinPreview
    {
        get
        {
            var shown = ShownPin;
            if (shown == null) return "";
            var text = shown.IsFile ? shown.Display : shown.Text;
            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return line.Trim();
        }
    }

    void NotifyPins()
    {
        OnPropertyChanged(nameof(HasPins));
        OnPropertyChanged(nameof(PinTitle));
        OnPropertyChanged(nameof(PinPreview));
    }

    void TogglePin(ChatMessage message)
    {
        var chat = ChatOf(message);
        if (chat == null) return;
        message.SetPinned(!message.IsPinned);
        if (message.IsPinned)
        {
            // The newest pin is the one the bar shows first.
            chat.PinCursor = chat.Messages.Count(m => m.IsPinned) - 1;
        }
        else
        {
            var count = chat.Messages.Count(m => m.IsPinned);
            chat.PinCursor = Math.Clamp(chat.PinCursor, 0, Math.Max(0, count - 1));
        }
        SaveHistory();
        if (ReferenceEquals(chat, CurrentChat)) NotifyPins();
    }

    // Tap on the bar: jump to the shown pinned message, then the bar moves on to the next older pin.
    [RelayCommand]
    void PinBarTap()
    {
        var pins = CurrentPins();
        var chat = CurrentChat;
        if (chat == null || pins.Count == 0) return;
        var index = Math.Clamp(chat.PinCursor, 0, pins.Count - 1);
        var target = pins[index];
        chat.PinCursor = index == 0 ? pins.Count - 1 : index - 1;
        NotifyPins();
        ScrollToMessageRequested?.Invoke(target);
    }

    [RelayCommand]
    void PinBarUnpin()
    {
        var shown = ShownPin;
        if (shown != null) TogglePin(shown);
    }

    async Task EditMessageAsync(ChatMessage message, Page page)
    {
        var loc = Loc.Instance;
        var s = session;
        var chat = ChatOf(message);
        if (s == null || !IsConnected || chat == null || !ReferenceEquals(chat, linkedChat))
        {
            await page.DisplayAlert(loc["editTitle"], loc["editNeedsConnection"], loc["ok"]);
            return;
        }
        var edited = await page.DisplayPromptAsync(loc["editTitle"], "", loc["ok"], loc["cancel"], null, -1, Keyboard.Text, message.Text);
        var text = edited?.Trim();
        if (string.IsNullOrEmpty(text) || text == message.Text) return;
        try
        {
            await s.SendTextEditAsync(message.MessageId, text);
            message.Text = text;
            message.IsEdited = true;
            SaveHistory();
            NotifyPins();
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "edit message failed", ex);
            SetStatus("failed");
        }
    }

    void ApplyReceivedEdit(Guid id, string text)
    {
        foreach (var chat in Conversations)
        {
            var message = chat.Messages.FirstOrDefault(m => !m.IsMine && m.IsText && m.MessageId == id);
            if (message == null) continue;
            message.Text = text;
            message.IsEdited = true;
            SaveHistory();
            NotifyPins();
            return;
        }
    }

    static async Task SafeAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", what + " failed", ex);
        }
    }

    [RelayCommand]
    void ToggleLanguage()
    {
        Loc.Instance.Toggle();
        LanguageIndex = Loc.Instance.IsFa ? 0 : 1;
        OnPropertyChanged(nameof(ThemeItems));
        SetStatus(statusKey);
        OnPropertyChanged(nameof(LinkedTitle));
        OnPropertyChanged(nameof(LocalAddresses));
        NotifySearch();
        NotifyTransfers();
        NotifyComposer();
        NotifyPins();
    }

    [RelayCommand]
    void ToggleDrawer() => IsDrawerOpen = !IsDrawerOpen;

    [RelayCommand]
    void CloseDrawer() => IsDrawerOpen = false;

    [RelayCommand]
    void OpenChat(Conversation? chat)
    {
        if (chat == null) return;
        CurrentChat = chat;
        IsDrawerOpen = false;
    }

    [RelayCommand]
    async Task ChatMenuAsync(Conversation? chat)
    {
        if (chat == null) return;
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        var loc = Loc.Instance;
        var choice = await page.DisplayActionSheet(chat.Name, loc["cancel"], null, loc["renameChat"], loc["deleteChat"]);
        if (choice == loc["renameChat"])
        {
            var input = await page.DisplayPromptAsync(loc["renameChat"], loc["renameChatAsk"], loc["save"], loc["cancel"],
                maxLength: 40, initialValue: chat.Name);
            if (input == null) return;
            var clean = LocalDevice.Clean(input);
            chat.Alias = clean.Length == 0 || clean == chat.PeerName ? null : clean;
            if (ReferenceEquals(chat, linkedChat) && session != null) session.ReceiveFolder = FolderFor(chat);
            SaveHistory();
            return;
        }
        if (choice != loc["deleteChat"]) return;
        if (ReferenceEquals(chat, linkedChat))
        {
            await page.DisplayAlert(chat.Name, loc["chatInUse"], "OK");
            return;
        }
        var ok = await page.DisplayAlert(chat.Name, loc["deleteChatAsk"], loc["delete"], loc["cancel"]);
        if (!ok) return;
        var removed = chat.Messages.ToList();
        var index = Conversations.IndexOf(chat);
        Conversations.Remove(chat);
        foreach (var m in removed) ForgetMessage(m);
        if (ReferenceEquals(chat, CurrentChat))
            CurrentChat = Conversations.Count > 0 ? Conversations[Math.Min(index, Conversations.Count - 1)] : null;
        SaveHistory();
        AppLog.Write("UI", "chat deleted");
    }

    public bool CanOpenBatterySettings => keepAlive.CanOpenBatterySettings;

    // ---- Battery guide (Settings): shows whether the phone restricts the app and the steps to fix it.
    public bool IsBatteryUnrestricted => keepAlive.IsBatteryUnrestricted;
    public string BatteryStatusText => Loc.Instance[IsBatteryUnrestricted ? "batteryStatusOk" : "batteryStatusLimited"];
    public Color BatteryStatusColor => IsBatteryUnrestricted ? Color.FromArgb("#22C55E") : Color.FromArgb("#F59E0B");
    public bool BatteryNeedsFix => !IsBatteryUnrestricted;

    public string BatteryBrandGuide => keepAlive.DeviceBrand switch
    {
        "xiaomi" or "redmi" or "poco" => Loc.Instance["batteryBrandXiaomi"],
        "samsung" => Loc.Instance["batteryBrandSamsung"],
        "huawei" or "honor" => Loc.Instance["batteryBrandHuawei"],
        "oppo" or "realme" or "oneplus" or "vivo" => Loc.Instance["batteryBrandOppo"],
        _ => ""
    };

    public bool HasBatteryBrandGuide => BatteryBrandGuide.Length > 0;

    public void RefreshBatteryStatus()
    {
        OnPropertyChanged(nameof(IsBatteryUnrestricted));
        OnPropertyChanged(nameof(BatteryStatusText));
        OnPropertyChanged(nameof(BatteryStatusColor));
        OnPropertyChanged(nameof(BatteryNeedsFix));
    }

    [RelayCommand]
    async Task BatterySettingsAsync()
    {
        AppLog.Write("UI", "battery settings pressed");
        await SafeAsync("battery settings", keepAlive.OpenBatterySettingsAsync);
    }

    [RelayCommand]
    void ToggleLog()
    {
        ShowLog = !ShowLog;
        if (ShowLog) IsDrawerOpen = false;
    }

    [RelayCommand]
    void CloseLog() => ShowLog = false;

    [RelayCommand]
    async Task ShowHelpAsync(string? topic)
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        var key = topic switch
        {
            "mode" => IsLanMode ? "Wifi" : "Bluetooth",
            "bluetooth" => "Bluetooth",
            "ip" => "Ip",
            "quick" => "Quick",
            _ => null
        };
        if (key == null) return;
        var loc = Loc.Instance;
        await page.DisplayAlert(loc["help" + key + "Title"], loc["help" + key], loc["ok"]);
    }

    [RelayCommand]
    async Task CopyLogAsync()
    {
        var text = AppLog.GetText();
        await Clipboard.Default.SetTextAsync(text);
        AppLog.Write("UI", $"log copied to clipboard chars={text.Length}");
    }

    [RelayCommand]
    void ClearLog() => AppLog.Clear();
}
