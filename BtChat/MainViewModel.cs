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
        if (value == 1) UpdateAddresses("switched to Wi-Fi mode");
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

    public MainViewModel(IBluetoothTransport transport, TcpTransport tcp, IReceivedFileStore files)
    {
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
                MainThread.BeginInvokeOnMainThread(() => UpdateAddresses("connectivity changed"));
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
        if (!await connectLock.WaitAsync(0))
        {
            AppLog.Write("TCP", "connect skipped, another connect in progress");
            return;
        }
        try
        {
            Host = address;
            Preferences.Default.Set("host", address);
            tcp.LogNetworkState("before connect");
            SetStatus("connecting");
            var stream = await tcp.ConnectAsync(address, CancellationToken.None);
            _ = RunSessionAsync(stream, false, "tcp-out");
        }
        catch (OperationCanceledException)
        {
            SetStatus("tcpTimeout");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            SetStatus("tcpRefused");
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "tcp connect failed", ex);
            SetStatus("failed");
        }
        finally
        {
            connectLock.Release();
        }
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
