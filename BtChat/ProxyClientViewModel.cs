using System.Globalization;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BtChat;

// Client side of "Internet sharing / Proxy": connect to another device's proxy (QR or manual),
// test it, and where the platform allows it use it as the system proxy.
public partial class ProxyClientViewModel : ObservableObject
{
    const string Tag = "ProxyClient";
    static readonly Color Green = Color.FromArgb("#22C55E");
    static readonly Color Red = Color.FromArgb("#EF4444");

    readonly TcpTransport tcp;
    readonly IQrScanner qr;
    readonly ISystemProxy system;
    readonly IVpnTunnel vpn;
    Timer? vpnTimer;

    // Set while the page is open, so dialogs appear on top of it.
    public Page? Host { get; set; }

    [ObservableProperty] string hostText = Preferences.Default.Get("pcHost", "");
    [ObservableProperty] string portText = Preferences.Default.Get("pcPort", "8080");
    [ObservableProperty] string username = Preferences.Default.Get("pcUser", "");
    [ObservableProperty] string password = Preferences.Default.Get("pcPass", "");
    [ObservableProperty] bool showPassword;
    [ObservableProperty] bool isBusy;
    [ObservableProperty] string statusText = "";
    [ObservableProperty] Color statusColor = Colors.Gray;
    [ObservableProperty] bool systemActive;
    [ObservableProperty] bool vpnActive;
    [ObservableProperty] string vpnStatsText = "";

    public ProxyClientViewModel(TcpTransport tcp, IQrScanner qr, ISystemProxy system, IVpnTunnel vpn)
    {
        this.vpn = vpn;
        this.tcp = tcp;
        this.qr = qr;
        this.system = system;
        Loc.Instance.PropertyChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(string.Empty));
        vpn.StateChanged += () => MainThread.BeginInvokeOnMainThread(SyncVpnState);

        // A proxy left behind by a crash or a kill would break the internet: put the old settings back.
        try
        {
            if (system.CanApply && system.IsApplied)
            {
                AppLog.Write(Tag, "restoring system proxy left from the last run");
                system.Restore();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "restore at start failed", ex);
        }
    }

    partial void OnHostTextChanged(string value) => Preferences.Default.Set("pcHost", value ?? "");
    partial void OnPortTextChanged(string value) => Preferences.Default.Set("pcPort", value ?? "");
    partial void OnUsernameChanged(string value) => Preferences.Default.Set("pcUser", value ?? "");
    partial void OnPasswordChanged(string value) => Preferences.Default.Set("pcPass", value ?? "");
    partial void OnShowPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordHidden));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnSystemActiveChanged(bool value) => OnPropertyChanged(nameof(SystemToggleText));
    partial void OnVpnActiveChanged(bool value) => OnPropertyChanged(nameof(VpnToggleText));

    public bool PasswordHidden => !ShowPassword;
    public bool IsNotBusy => !IsBusy;
    public bool HasStatus => StatusText.Length > 0;
    public bool CanScanQr => qr.IsSupported;
    public bool CanApplySystem => system.CanApply;
    public bool ShowManualHint => !system.CanApply;
    public bool CanOpenSettings => system.CanOpenNetworkSettings;
    public bool CanVpn => vpn.IsSupported;
    public string VpnToggleText => Loc.Instance[VpnActive ? "pcVpnOff" : "pcVpnOn"];
    public string SystemToggleText => Loc.Instance[SystemActive ? "pcSystemOff" : "pcSystemOn"];

    void SetStatus(string text, Color color)
    {
        StatusText = text;
        StatusColor = color;
    }

    // Reads and checks the form; shows the problem in the status line when something is wrong.
    bool TryGetTarget(out string host, out int port, out string? user, out string? pass)
    {
        var loc = Loc.Instance;
        host = HostText.Trim();
        port = 0;
        user = null;
        pass = null;
        if (host.Length == 0 || host.Contains(' '))
        {
            SetStatus(loc["pcNeedHost"], Red);
            return false;
        }
        if (!int.TryParse(PortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
        {
            SetStatus(loc["proxyBadPort"], Red);
            return false;
        }
        if (Username.Trim().Length > 0)
        {
            user = Username.Trim();
            pass = Password;
        }
        return true;
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    async Task TestAsync()
    {
        if (IsBusy) return;
        if (!TryGetTarget(out var host, out var port, out var user, out var pass)) return;
        var loc = Loc.Instance;
        IsBusy = true;
        SetStatus(loc["pcTesting"], Colors.Gray);
        try
        {
            var result = await ProxyProbe.RunAsync(host, port, user, pass, CancellationToken.None);
            AppLog.Write(Tag, $"test {host}:{port} -> {result.Status} {result.LatencyMs} ms {result.Detail}");
            ShowResult(result, host, port);
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "test failed", ex);
            SetStatus(string.Format(loc["pcError"], ex.Message), Red);
        }
        finally
        {
            IsBusy = false;
        }
    }

    void ShowResult(ProbeResult r, string host, int port)
    {
        var loc = Loc.Instance;
        switch (r.Status)
        {
            case ProbeStatus.Ok:
                SetStatus(string.Format(loc["pcOk"], r.LatencyMs), Green);
                break;
            case ProbeStatus.Unreachable:
                SetStatus(string.Format(loc["pcUnreachable"], host, port), Red);
                break;
            case ProbeStatus.Timeout:
                SetStatus(loc["pcTimeout"], Red);
                break;
            case ProbeStatus.Rejected:
                SetStatus(loc["pcRejected"], Red);
                break;
            case ProbeStatus.AuthFailed:
                SetStatus(loc["pcAuth"], Red);
                break;
            case ProbeStatus.TargetRefused:
                SetStatus(string.Format(loc["pcTargetRefused"], r.Detail), Red);
                break;
            case ProbeStatus.NotAProxy:
                SetStatus(loc["pcNotProxy"], Red);
                break;
            default:
                SetStatus(string.Format(loc["pcError"], r.Detail), Red);
                break;
        }
    }

    [RelayCommand]
    async Task ScanQrAsync()
    {
        if (!qr.IsSupported || IsBusy) return;
        var loc = Loc.Instance;
        AppLog.Write(Tag, "scan pressed");
        string? text;
        try
        {
            text = await qr.ScanAsync();
        }
        catch (PermissionException ex)
        {
            AppLog.Error(Tag, "camera permission denied", ex);
            SetStatus(loc["qrNoCamera"], Red);
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "scan failed", ex);
            SetStatus(loc["qrFailed"], Red);
            return;
        }
        if (text == null) return;

        if (!ProxyQrPayload.TryParse(text, out var info))
        {
            AppLog.Write(Tag, "scanned a QR that is not a proxy code");
            SetStatus(loc["pcQrInvalid"], Red);
            return;
        }

        IsBusy = true;
        SetStatus(loc["pcTesting"], Colors.Gray);
        try
        {
            // several addresses (Wi-Fi + hotspot ...): take the first one that answers
            var candidates = tcp.OrderCandidates(info.Addresses).ToList();
            if (candidates.Count == 0) candidates = info.Addresses.ToList();
            var chosen = await PickReachableAsync(candidates, info.Port) ?? candidates[0];
            HostText = chosen;
            PortText = info.Port.ToString(CultureInfo.InvariantCulture);
            Username = info.User ?? "";
            Password = info.Password ?? "";
            AppLog.Write(Tag, $"qr: '{info.Name}' addresses=[{string.Join(", ", info.Addresses)}] chosen={chosen}:{info.Port} (credentials in qr: {info.User != null})");
        }
        finally
        {
            IsBusy = false;
        }
        await TestAsync();
    }

    static async Task<string?> PickReachableAsync(IReadOnlyList<string> candidates, int port)
    {
        if (candidates.Count == 1) return candidates[0];
        var results = await Task.WhenAll(candidates.Select(c => CanConnectAsync(c, port)));
        return candidates.FirstOrDefault(c => results.Contains(c));
    }

    static async Task<string?> CanConnectAsync(string address, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(2000);
            await client.ConnectAsync(address, port, cts.Token);
            return address;
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    void ToggleSystemProxy()
    {
        var loc = Loc.Instance;
        if (SystemActive)
        {
            try { system.Restore(); }
            catch (Exception ex) { AppLog.Error(Tag, "restore failed", ex); }
            SystemActive = false;
            SetStatus(loc["pcSystemRestored"], Colors.Gray);
            return;
        }
        if (!TryGetTarget(out var host, out var port, out var user, out _)) return;
        try
        {
            system.Apply(host, port);
            SystemActive = true;
            var text = string.Format(loc["pcSystemActive"], host, port);
            if (user != null) text += "\n" + loc["pcSystemAuthNote"];
            SetStatus(text, Green);
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "apply failed", ex);
            SetStatus(string.Format(loc["pcSystemFailed"], ex.Message), Red);
        }
    }

    void SyncVpnState()
    {
        var running = vpn.IsRunning;
        if (running != VpnActive)
        {
            VpnActive = running;
            if (!running)
            {
                vpnTimer?.Dispose();
                vpnTimer = null;
                VpnStatsText = "";
                if (!IsBusy) SetStatus(Loc.Instance["pcVpnStopped"], Colors.Gray);
            }
        }
        if (running && vpnTimer == null)
            vpnTimer = new Timer(_ => MainThread.BeginInvokeOnMainThread(UpdateVpnStats), null, 1000, 1000);
        UpdateVpnStats();
    }

    void UpdateVpnStats()
    {
        if (!vpn.IsRunning) return;
        VpnStatsText = string.Format(Loc.Instance["pcVpnStats"], ProxyViewModel.FormatBytes(vpn.BytesDown),
            ProxyViewModel.FormatBytes(vpn.BytesUp), vpn.ActiveFlows);
    }

    [RelayCommand]
    async Task ToggleVpnAsync()
    {
        if (IsBusy) return;
        var loc = Loc.Instance;
        if (vpn.IsRunning)
        {
            vpn.Stop();
            return;
        }
        if (!TryGetTarget(out var host, out var port, out var user, out var pass)) return;
        IsBusy = true;
        SetStatus(loc["pcTesting"], Colors.Gray);
        try
        {
            var probe = await ProxyProbe.RunAsync(host, port, user, pass, CancellationToken.None);
            AppLog.Write(Tag, $"vpn pre-test {host}:{port} -> {probe.Status} {probe.Detail}");
            if (probe.Status != ProbeStatus.Ok)
            {
                ShowResult(probe, host, port);
                return;
            }
            var result = await vpn.StartAsync(host, port, user, pass);
            switch (result)
            {
                case VpnStartResult.Started:
                    SetStatus(loc["pcVpnStarted"], Green);
                    break;
                case VpnStartResult.PermissionDenied:
                    SetStatus(loc["pcVpnDenied"], Red);
                    break;
                default:
                    SetStatus(string.Format(loc["pcVpnFailed"], vpn.LastError ?? "?"), Red);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "vpn toggle failed", ex);
            SetStatus(string.Format(loc["pcError"], ex.Message), Red);
        }
        finally
        {
            IsBusy = false;
            SyncVpnState();
        }
    }

    [RelayCommand]
    async Task CopyAddressAsync()
    {
        var host = HostText.Trim();
        if (host.Length == 0)
        {
            SetStatus(Loc.Instance["pcNeedHost"], Red);
            return;
        }
        try
        {
            await Clipboard.Default.SetTextAsync($"{host}:{PortText.Trim()}");
            SetStatus(Loc.Instance["proxyCopied"], Green);
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "copy failed", ex);
        }
    }

    [RelayCommand]
    void OpenNetworkSettings()
    {
        try { system.OpenNetworkSettings(); }
        catch (Exception ex) { AppLog.Error(Tag, "open settings failed", ex); }
    }

    [RelayCommand]
    void ToggleShowPassword() => ShowPassword = !ShowPassword;
}
