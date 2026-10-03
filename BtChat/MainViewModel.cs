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
    QrShowPage? qrPage;
    bool searched;
    readonly object gate = new();
    readonly SemaphoreSlim connectLock = new(1, 1);
    ChatSession? session;
    BtDevice? autoTarget;
    string statusKey = "idle";
    bool started;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
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
    int modeIndex = 0;

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

    IReadOnlyList<string> addresses = Array.Empty<string>();
    public string LocalAddresses => addresses.Count > 0 ? string.Join("  |  ", addresses) : Loc.Instance["noNetwork"];
    public bool HasMessages => Messages.Count > 0;
    public bool IsLinked => IsConnected || autoTarget != null;
    public bool IsNotLinked => !IsLinked;

    partial void OnModeIndexChanged(int value)
    {
        if (value < 0) { ModeIndex = 0; return; }
        AppLog.Write("UI", $"mode changed to {value}");
        if (IsLinked) Disconnect();
        discovery.SetActive(value == 1);
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

    partial void OnIsConnectedChanged(bool value) => RaiseLinked();

    void RaiseLinked()
    {
        OnPropertyChanged(nameof(IsLinked));
        OnPropertyChanged(nameof(IsNotLinked));
    }

    public MainViewModel(IBluetoothTransport transport, TcpTransport tcp, IReceivedFileStore files, IQrScanner qr, DiscoveryService discovery)
    {
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
        this.tcp = tcp;
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));
        foreach (var m in ChatHistory.Load()) Messages.Add(m);
        AppLog.Changed += () =>
        {
            if (ShowLog) MainThread.BeginInvokeOnMainThread(() => LogText = AppLog.GetText(400));
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
        AppLog.Write("APP", $"start {DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString} {DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}");
        UpdateAddresses("startup");
        _ = Task.Run(() => AcceptLoopAsync("tcp", tcp.AcceptAsync));
        _ = Task.Run(discovery.RunAsync);
        await files.EnsureReadyAsync();
        if (!await transport.EnsurePermissionsAsync())
        {
            AppLog.Write("APP", "bluetooth not usable, skipping bluetooth loops");
            SetStatus("btoff");
            return;
        }
        await RefreshAsync();
        var savedId = Preferences.Default.Get("autoBt", "");
        AppLog.Write("APP", $"saved auto target id='{savedId}'");
        if (savedId.Length > 0)
        {
            var saved = Devices.FirstOrDefault(d => d.Id == savedId);
            if (saved != null)
            {
                SelectedDevice = saved;
                autoTarget = saved;
            }
            else
            {
                AppLog.Write("APP", "saved auto target not found in paired devices");
            }
        }
        _ = Task.Run(() => AcceptLoopAsync("bt", transport.AcceptAsync));
        _ = Task.Run(ReconnectLoopAsync);
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
            if (target == null || session != null)
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
            if (session != null)
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

    async Task RunSessionAsync(Stream stream, bool inbound, string name)
    {
        ChatSession current;
        lock (gate)
        {
            if (session != null)
            {
                AppLog.Write("VM", $"DROPPING {name} stream because a session already exists");
                stream.Dispose();
                return;
            }
            current = new ChatSession(stream, files, name);
            session = current;
        }
        AppLog.Write("VM", $"session started {name} inbound={inbound}");
        if (inbound)
        {
            autoTarget = null;
            Preferences.Default.Remove("autoBt");
        }
        current.MessageReceived += m => MainThread.BeginInvokeOnMainThread(() => AddMessage(m));
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
            IsConnected = false;
            AppLog.Write("VM", $"session {name} finished, autoTarget={(autoTarget != null ? autoTarget.Name : "none")}");
            SetStatus(autoTarget != null ? "retrying" : "idle");
        }
    }

    [RelayCommand]
    async Task RefreshAsync()
    {
        var list = await transport.GetPairedDevicesAsync();
        Devices.Clear();
        foreach (var d in list) Devices.Add(d);
    }

    [RelayCommand]
    async Task ConnectAsync()
    {
        var target = SelectedDevice;
        AppLog.Write("UI", $"Connect pressed target={target?.Name} sessionExists={session != null}");
        if (target == null || session != null) return;
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
    async Task ConnectViaTcpAsync(IReadOnlyList<string> candidates, int timeoutSeconds = 8)
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
                    _ = RunSessionAsync(stream, false, "tcp-out");
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
        var payload = QrPayload.Build(addresses, DiscoveryService.DeviceName());
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
        await ConnectViaTcpAsync(candidates, 4);
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
        var text = Draft.Trim();
        if (s == null || text.Length == 0) return;
        Draft = "";
        try
        {
            await s.SendTextAsync(text);
            AddMessage(new ChatMessage { Text = text, IsMine = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "send text failed", ex);
            SetStatus("failed");
        }
    }

    [RelayCommand]
    async Task AttachAsync()
    {
        var s = session;
        if (s == null) return;
        var picked = await FilePicker.Default.PickAsync();
        if (picked == null) return;
        var cts = new CancellationTokenSource();
        var message = new ChatMessage { Text = picked.FileName, IsMine = true, IsFile = true, Location = picked.FullPath, ShowProgress = true, Cts = cts };
        AddMessage(message);
        try
        {
            await using var source = await picked.OpenReadAsync();
            await Task.Run(() => s.SendFileAsync(picked.FileName, source, (done, total) => message.Report(done, total), cts.Token));
            message.Complete();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            AppLog.Write("VM", "send file canceled by user");
            message.Fail("fileCanceled");
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "send file failed", ex);
            message.Fail();
            SetStatus("failed");
        }
        finally
        {
            message.Cts = null;
        }
    }

    [RelayCommand]
    void CancelFile(ChatMessage? message)
    {
        if (message?.Cts == null) return;
        AppLog.Write("UI", $"cancel sending {message.Text}");
        message.Cts.Cancel();
    }

    void AddMessage(ChatMessage message)
    {
        message.Finished += SaveHistory;
        Messages.Add(message);
        if (!message.ShowProgress) SaveHistory();
    }

    void SaveHistory() => ChatHistory.Save(Messages);

    [RelayCommand]
    async Task ClearHistoryAsync()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        var loc = Loc.Instance;
        var ok = await page.DisplayAlert(loc["clearHistory"], loc["clearHistoryAsk"], loc["delete"], loc["cancel"]);
        if (!ok) return;
        // Transfers that are still running keep their bubble.
        foreach (var m in Messages.Where(m => !m.ShowProgress).ToList()) Messages.Remove(m);
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
        Messages.Remove(message);
        SaveHistory();
    }

    [RelayCommand]
    async Task OpenFileAsync(ChatMessage? message)
    {
        if (message?.Location == null || message.ShowProgress || message.Failed) return;
        await SafeAsync("open file", () => files.OpenAsync(message.Location, message.Text));
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
            if (!message.Failed && message.Location != null)
            {
                options.Add(loc["openFile"]);
                if (message.IsReceivedFile) options.Add(loc["openFolder"]);
                options.Add(loc["share"]);
            }
            var deleteLabel = message.IsReceivedFile && !message.Failed ? loc["deleteFile"] : loc["deleteMessage"];
            options.Add(deleteLabel);
            var picked = await page.DisplayActionSheet(message.Text, loc["cancel"], null, options.ToArray());
            if (picked == null) return;
            if (picked == deleteLabel)
                await DeleteMessageAsync(message, page);
            else if (picked == loc["openFile"])
                await SafeAsync("open file", () => files.OpenAsync(message.Location!, message.Text));
            else if (picked == loc["openFolder"])
                await SafeAsync("open folder", () => files.ShowInFolderAsync(message.Location!));
            else if (picked == loc["share"])
                await SafeAsync("share file", () => files.ShareAsync(message.Location!, message.Text));
            return;
        }
        var choice = await page.DisplayActionSheet(null, loc["cancel"], null, loc["copyText"], loc["share"], loc["deleteMessage"]);
        if (choice == loc["copyText"])
            await Clipboard.Default.SetTextAsync(message.Text);
        else if (choice == loc["share"])
            await Share.Default.RequestAsync(new ShareTextRequest { Text = message.Text });
        else if (choice == loc["deleteMessage"])
        {
            Messages.Remove(message);
            SaveHistory();
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
        SetStatus(statusKey);
        OnPropertyChanged(nameof(LocalAddresses));
        NotifySearch();
    }

    [RelayCommand]
    void ToggleLog() => ShowLog = !ShowLog;

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
