using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BtChat;

public partial class MainViewModel : ObservableObject
{
    readonly IBluetoothTransport transport;
    readonly TcpTransport tcp;
    readonly string receiveDir = Path.Combine(FileSystem.AppDataDirectory, "received");
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
    [ObservableProperty] string localAddresses = "";
    [ObservableProperty] bool showLog;
    [ObservableProperty] string logText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBluetoothMode))]
    [NotifyPropertyChangedFor(nameof(IsLanMode))]
    int modeIndex = 0;

    public bool IsBluetoothMode => ModeIndex == 0;
    public bool IsLanMode => ModeIndex == 1;
    public bool IsLinked => IsConnected || autoTarget != null;
    public bool IsNotLinked => !IsLinked;

    partial void OnModeIndexChanged(int value)
    {
        if (value < 0) { ModeIndex = 0; return; }
        AppLog.Write("UI", $"mode changed to {value}");
        if (IsLinked) Disconnect();
    }

    partial void OnIsConnectedChanged(bool value) => RaiseLinked();

    void RaiseLinked()
    {
        OnPropertyChanged(nameof(IsLinked));
        OnPropertyChanged(nameof(IsNotLinked));
    }

    public MainViewModel(IBluetoothTransport transport, TcpTransport tcp)
    {
        this.transport = transport;
        this.tcp = tcp;
        AppLog.Changed += () =>
        {
            if (ShowLog) MainThread.BeginInvokeOnMainThread(() => LogText = AppLog.GetText(400));
        };
        SetStatus("idle");
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
        LocalAddresses = string.Join("  |  ", tcp.GetLocalAddresses());
        _ = Task.Run(() => AcceptLoopAsync("tcp", tcp.AcceptAsync));
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
            current = new ChatSession(stream, receiveDir, name);
            session = current;
        }
        AppLog.Write("VM", $"session started {name} inbound={inbound}");
        if (inbound)
        {
            autoTarget = null;
            Preferences.Default.Remove("autoBt");
        }
        current.MessageReceived += m => MainThread.BeginInvokeOnMainThread(() => Messages.Add(m));
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
        LocalAddresses = string.Join("  |  ", tcp.GetLocalAddresses());
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

    [RelayCommand]
    async Task ConnectLanAsync()
    {
        var address = Host.Trim();
        AppLog.Write("UI", $"Connect via IP pressed host={address}");
        if (address.Length == 0 || session != null) return;
        Preferences.Default.Set("host", address);
        SetStatus("connecting");
        try
        {
            var stream = await tcp.ConnectAsync(address, CancellationToken.None);
            _ = RunSessionAsync(stream, false, "tcp-out");
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "tcp connect failed", ex);
            SetStatus("failed");
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
            Messages.Add(new ChatMessage { Text = text, IsMine = true });
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
        var message = new ChatMessage { Text = picked.FileName, IsMine = true, IsFile = true, FilePath = picked.FullPath, ShowProgress = true };
        Messages.Add(message);
        try
        {
            await using var source = await picked.OpenReadAsync();
            await Task.Run(() => s.SendFileAsync(picked.FileName, source, (done, total) => message.Report(done, total)));
            message.Complete();
        }
        catch (Exception ex)
        {
            AppLog.Error("VM", "send file failed", ex);
            message.Fail();
            SetStatus("failed");
        }
    }

    [RelayCommand]
    async Task OpenFileAsync(ChatMessage? message)
    {
        if (message?.FilePath == null || message.ShowProgress || message.Failed) return;
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = message.Text,
            File = new ShareFile(message.FilePath)
        });
    }

    [RelayCommand]
    async Task MessageMenuAsync(ChatMessage? message)
    {
        if (message == null || message.IsFile) return;
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        var loc = Loc.Instance;
        var choice = await page.DisplayActionSheet(null, loc["cancel"], null, loc["copyText"], loc["share"]);
        if (choice == loc["copyText"])
            await Clipboard.Default.SetTextAsync(message.Text);
        else if (choice == loc["share"])
            await Share.Default.RequestAsync(new ShareTextRequest { Text = message.Text });
    }

    [RelayCommand]
    void ToggleLanguage()
    {
        Loc.Instance.Toggle();
        SetStatus(statusKey);
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
