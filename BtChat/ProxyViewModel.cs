using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BtChat;

public sealed class ProxyClientRow
{
    public string Address { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool LimitReached { get; init; }
    public string Badge => Loc.Instance["proxyClientLimit"];
}

// Server side of "Internet sharing / Proxy": settings, start/stop and live statistics.
public partial class ProxyViewModel : ObservableObject
{
    const string Tag = "ProxyUI";
    static readonly Color Green = Color.FromArgb("#22C55E");
    static readonly Color Red = Color.FromArgb("#EF4444");
    static readonly Color Orange = Color.FromArgb("#F59E0B");

    static readonly ProxySnapshot Empty =
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, Array.Empty<ProxyClientInfo>(), false, 0);

    readonly TcpTransport tcp;
    readonly IQrScanner qr;
    readonly ProxyServer server = new();
    ProxySnapshot snapshot = Empty;
    string clientSignature = "";

    // Set by the page while it is open, so dialogs and the QR page appear on top of it.
    Page? host;
    public Page? Host
    {
        get => host;
        set
        {
            host = value;
            Client.Host = value;
        }
    }

    // Client side (connect to another device's proxy).
    public ProxyClientViewModel Client { get; }

    // Raised on the UI thread after the server started or stopped.
    public event Action? RunningChanged;

    public ObservableCollection<string> Addresses { get; } = new();
    public ObservableCollection<ProxyClientRow> Clients { get; } = new();
    public string[] UnitItems { get; } = { "MB", "GB" };

    [ObservableProperty] string portText = Preferences.Default.Get("proxyPort", "8080");
    [ObservableProperty] bool authEnabled = Preferences.Default.Get("proxyAuth", false);
    [ObservableProperty] string username = Preferences.Default.Get("proxyUser", "");
    [ObservableProperty] string password = Preferences.Default.Get("proxyPass", "");
    [ObservableProperty] bool showPassword;
    [ObservableProperty] bool localOnly = Preferences.Default.Get("proxyLocalOnly", true);
    [ObservableProperty] string totalLimitText = Preferences.Default.Get("proxyTotalLimit", "");
    [ObservableProperty] int totalLimitUnit = Preferences.Default.Get("proxyTotalUnit", 1) == 0 ? 0 : 1;
    [ObservableProperty] string clientLimitText = Preferences.Default.Get("proxyClientLimit", "");
    [ObservableProperty] int clientLimitUnit = Preferences.Default.Get("proxyClientUnit", 1) == 0 ? 0 : 1;
    [ObservableProperty] bool isRunning;
    [ObservableProperty] string errorText = "";
    [ObservableProperty] bool copied;

    // Reverse connection (this device dials out to a receiving device instead of waiting for clients).
    [ObservableProperty] int shareKindIndex = Preferences.Default.Get("proxyKind", 0) == 1 ? 1 : 0;
    [ObservableProperty] string reverseHost = Preferences.Default.Get("proxyRvHost", "");
    [ObservableProperty] string reversePortText = Preferences.Default.Get("proxyRvPort", "8081");
    [ObservableProperty] string pairPassword = Preferences.Default.Get("proxyRvPass", "");
    [ObservableProperty] bool showPairPassword;

    public string[] ShareKindItems => new[] { Loc.Instance["proxyKindDirect"], Loc.Instance["proxyKindReverse"] };
    public bool ShowDirect => ShareKindIndex == 0;
    public bool ShowReverse => ShareKindIndex == 1;
    public bool PairPasswordHidden => !ShowPairPassword;
    public bool CanScanReceiverQr => qr.IsSupported;

    partial void OnShareKindIndexChanged(int value)
    {
        Preferences.Default.Set("proxyKind", value);
        OnPropertyChanged(nameof(ShowDirect));
        OnPropertyChanged(nameof(ShowReverse));
    }

    partial void OnReverseHostChanged(string value) => Preferences.Default.Set("proxyRvHost", value ?? "");
    partial void OnReversePortTextChanged(string value) => Preferences.Default.Set("proxyRvPort", value ?? "");
    partial void OnPairPasswordChanged(string value) => Preferences.Default.Set("proxyRvPass", value ?? "");
    partial void OnShowPairPasswordChanged(bool value) => OnPropertyChanged(nameof(PairPasswordHidden));

    // 0 = this device shares its internet (server), 1 = this device uses another device's proxy (client).
    [ObservableProperty] int modeIndex = Preferences.Default.Get("proxyMode", 0) == 1 ? 1 : 0;

    public string[] ModeItems => new[] { Loc.Instance["proxyModeServer"], Loc.Instance["proxyModeClient"] };
    public bool ShowServer => ModeIndex == 0;
    public bool ShowClient => ModeIndex == 1;

    partial void OnModeIndexChanged(int value)
    {
        Preferences.Default.Set("proxyMode", value);
        OnPropertyChanged(nameof(ShowServer));
        OnPropertyChanged(nameof(ShowClient));
    }

    public ProxyViewModel(TcpTransport tcp, ProxyClientViewModel client, IQrScanner qr)
    {
        this.tcp = tcp;
        this.qr = qr;
        Client = client;
        client.ReceiverChanged += () =>
        {
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(DotColor));
            OnPropertyChanged(nameof(IsActive));
            RunningChanged?.Invoke();
        };
        server.StatsChanged += s => MainThread.BeginInvokeOnMainThread(() => Apply(s));
        Loc.Instance.PropertyChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(string.Empty));
        RefreshAddresses();
    }

    // ---------------------------------------------------------------- saved settings

    partial void OnPortTextChanged(string value)
    {
        Preferences.Default.Set("proxyPort", value ?? "");
        if (!IsRunning) RefreshAddresses();
    }

    partial void OnAuthEnabledChanged(bool value) => Preferences.Default.Set("proxyAuth", value);
    partial void OnUsernameChanged(string value) => Preferences.Default.Set("proxyUser", value ?? "");
    partial void OnPasswordChanged(string value) => Preferences.Default.Set("proxyPass", value ?? "");
    partial void OnLocalOnlyChanged(bool value) => Preferences.Default.Set("proxyLocalOnly", value);
    partial void OnShowPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordHidden));

    partial void OnTotalLimitTextChanged(string value)
    {
        Preferences.Default.Set("proxyTotalLimit", value ?? "");
        ApplyLimitsLive();
    }

    partial void OnTotalLimitUnitChanged(int value)
    {
        Preferences.Default.Set("proxyTotalUnit", value);
        ApplyLimitsLive();
    }

    partial void OnClientLimitTextChanged(string value)
    {
        Preferences.Default.Set("proxyClientLimit", value ?? "");
        ApplyLimitsLive();
    }

    partial void OnClientLimitUnitChanged(int value)
    {
        Preferences.Default.Set("proxyClientUnit", value);
        ApplyLimitsLive();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotRunning));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(ToggleColor));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(DotColor));
        OnPropertyChanged(nameof(ShowStats));
        OnPropertyChanged(nameof(HasCap));
        OnPropertyChanged(nameof(CanShowQr));
    }

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    // Caps can be changed while sharing; an invalid text is ignored until it is fixed.
    void ApplyLimitsLive()
    {
        if (!IsRunning) return;
        if (!TryParseLimit(TotalLimitText, TotalLimitUnit, out var total)) return;
        if (!TryParseLimit(ClientLimitText, ClientLimitUnit, out var perClient)) return;
        server.UpdateLimits(total, perClient);
    }

    // ---------------------------------------------------------------- state for the UI

    public bool IsNotRunning => !IsRunning;

    // Sharing or receiving: either keeps the background service alive.
    public bool IsActive => IsRunning || Client.ReceiverRunning;
    public bool PasswordHidden => !ShowPassword;
    public bool HasError => ErrorText.Length > 0;
    public bool CanShowQr => IsRunning;
    public bool HasAddresses => Addresses.Count > 0;
    public bool NoAddresses => Addresses.Count == 0;
    public bool HasClients => Clients.Count > 0;
    public bool ShowStats => IsRunning || snapshot.TotalDown + snapshot.TotalUp > 0;
    public bool IsLimitReached => snapshot.TotalLimitReached;
    public bool HasCap => IsRunning && snapshot.TotalLimitBytes > 0;

    public string ToggleText => Loc.Instance[IsRunning ? "proxyStop" : "proxyStart"];
    public Color ToggleColor => IsRunning ? Red : Green;
    public Color DotColor => IsRunning ? (IsLimitReached ? Orange : Green) : (Client.ReceiverRunning ? Green : Colors.Gray);

    // Short line for the side drawer.
    public string StatusLine => IsRunning
        ? (snapshot.Reverse
            ? string.Format(Loc.Instance["proxyStatusReverse"], ReverseHost.Trim(), snapshot.ReverseReady)
            : string.Format(Loc.Instance["proxyStatusRunning"], server.Port, snapshot.ActiveClients))
        : Client.ReceiverRunning
            ? string.Format(Loc.Instance["rvStatusRunning"], Client.LinkPortText.Trim(), Client.LocalPortText.Trim())
            : Loc.Instance["proxyDrawerHint"];

    public string NotificationText => IsRunning
        ? (server.IsReverse
            ? string.Format(Loc.Instance["proxyNotifReverse"], ReverseHost.Trim())
            : string.Format(Loc.Instance["proxyNotif"], server.Port))
        : string.Format(Loc.Instance["rvNotif"], Client.LinkPortText.Trim());

    public string ConnectionsText => snapshot.ActiveConnections.ToString(CultureInfo.InvariantCulture);
    public string DevicesText => snapshot.ActiveClients.ToString(CultureInfo.InvariantCulture);
    public string SpeedDownText => FormatBytes(snapshot.SpeedDown) + "/s";
    public string SpeedUpText => FormatBytes(snapshot.SpeedUp) + "/s";
    public string TotalDownText => FormatBytes(snapshot.TotalDown);
    public string TotalUpText => FormatBytes(snapshot.TotalUp);

    public string CapText => snapshot.TotalLimitBytes > 0
        ? string.Format(Loc.Instance["proxyCapUsed"], FormatBytes(snapshot.TotalDown + snapshot.TotalUp), FormatBytes(snapshot.TotalLimitBytes))
        : "";

    public double CapProgress => snapshot.TotalLimitBytes > 0
        ? Math.Clamp((double)(snapshot.TotalDown + snapshot.TotalUp) / snapshot.TotalLimitBytes, 0, 1)
        : 0;

    void Apply(ProxySnapshot s)
    {
        snapshot = s;
        var signature = string.Join("|", s.Clients.Select(c => $"{c.Address},{c.Connections},{c.BytesDown},{c.BytesUp},{c.LimitReached}"));
        if (signature != clientSignature)
        {
            clientSignature = signature;
            Clients.Clear();
            foreach (var c in s.Clients)
            {
                Clients.Add(new ProxyClientRow
                {
                    Address = c.Address,
                    Detail = string.Format(Loc.Instance["proxyClientDetail"], c.Connections, FormatBytes(c.BytesDown), FormatBytes(c.BytesUp)),
                    LimitReached = c.LimitReached
                });
            }
            OnPropertyChanged(nameof(HasClients));
        }
        OnPropertyChanged(nameof(ConnectionsText));
        OnPropertyChanged(nameof(DevicesText));
        OnPropertyChanged(nameof(SpeedDownText));
        OnPropertyChanged(nameof(SpeedUpText));
        OnPropertyChanged(nameof(TotalDownText));
        OnPropertyChanged(nameof(TotalUpText));
        OnPropertyChanged(nameof(CapText));
        OnPropertyChanged(nameof(CapProgress));
        OnPropertyChanged(nameof(HasCap));
        OnPropertyChanged(nameof(IsLimitReached));
        OnPropertyChanged(nameof(ShowStats));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(DotColor));
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    void Toggle()
    {
        if (IsRunning)
        {
            Stop();
            return;
        }

        ErrorText = "";
        var loc = Loc.Instance;
        var port = 0;
        var reversePort = 0;
        string? user = null, pass = null;
        if (ShowReverse)
        {
            var target = ReverseHost.Trim();
            if (target.Length == 0 || target.Contains(' ')) { ErrorText = loc["proxyNeedReceiver"]; return; }
            if (!int.TryParse(Normalize(ReversePortText).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out reversePort) || reversePort < 1 || reversePort > 65535)
            {
                ErrorText = loc["proxyBadPort"];
                return;
            }
            // the pairing password travels in the options like a proxy password (the proxy login itself is not used)
            if (PairPassword.Length > 0)
            {
                user = "pair";
                pass = PairPassword;
            }
            port = reversePort;
        }
        else
        {
            if (!int.TryParse(Normalize(PortText).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
            {
                ErrorText = loc["proxyBadPort"];
                return;
            }
            if (AuthEnabled)
            {
                user = Username.Trim();
                pass = Password;
                if (user.Length == 0 || pass.Length == 0) { ErrorText = loc["proxyNeedCreds"]; return; }
                if (user.Contains(':')) { ErrorText = loc["proxyBadUser"]; return; }
            }
        }
        if (!TryParseLimit(TotalLimitText, TotalLimitUnit, out var total) ||
            !TryParseLimit(ClientLimitText, ClientLimitUnit, out var perClient))
        {
            ErrorText = loc["proxyBadLimit"];
            return;
        }

        var options = new ProxyOptions
        {
            Port = port,
            Username = user,
            Password = pass,
            LocalNetworkOnly = LocalOnly,
            TotalLimitBytes = total,
            PerClientLimitBytes = perClient
        };
        try
        {
            if (ShowReverse)
                server.StartReverse(options, ReverseHost.Trim(), reversePort);
            else
                server.Start(options);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            ErrorText = string.Format(loc["proxyPortBusy"], port);
            return;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            ErrorText = string.Format(loc["proxyPortDenied"], port);
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "start failed", ex);
            ErrorText = string.Format(loc["proxyStartFailed"], ex.Message);
            return;
        }

        IsRunning = true;
        tcp.LogNetworkState("proxy start");
        RefreshAddresses();
        Apply(server.GetSnapshot());
        RunningChanged?.Invoke();
    }

    // Stops sharing and receiving (used when the app is closed).
    public void StopAll()
    {
        Client.StopReceiver();
        Stop();
    }

    public void Stop()
    {
        if (!server.IsRunning && !IsRunning) return;
        server.Stop();
        IsRunning = false;
        Apply(server.GetSnapshot());
        RefreshAddresses();
        RunningChanged?.Invoke();
    }

    [RelayCommand]
    void ResetCounters()
    {
        server.ResetCounters();
        Apply(server.GetSnapshot());
    }

    [RelayCommand]
    void ToggleShowPassword() => ShowPassword = !ShowPassword;

    [RelayCommand]
    void RefreshAddresses()
    {
        var port = CurrentPort();
        Addresses.Clear();
        foreach (var a in tcp.GetLocalAddresses()) Addresses.Add($"{a}:{port}");
        OnPropertyChanged(nameof(HasAddresses));
        OnPropertyChanged(nameof(NoAddresses));
    }

    [RelayCommand]
    async Task CopyAddressAsync(string? address)
    {
        if (string.IsNullOrEmpty(address)) return;
        try
        {
            await Clipboard.Default.SetTextAsync(address);
            Copied = true;
            await Task.Delay(1500);
            Copied = false;
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "copy failed", ex);
        }
    }

    [RelayCommand]
    async Task ShowQrAsync()
    {
        var page = Host ?? Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null || !server.IsRunning) return;
        var loc = Loc.Instance;
        RefreshAddresses();
        var addresses = tcp.GetLocalAddresses();
        if (addresses.Count == 0)
        {
            await page.DisplayAlert(loc["showQr"], loc["noNetwork"], loc["ok"]);
            return;
        }

        // The user decides each time whether the password goes into the QR code.
        string? user = null, pass = null;
        if (AuthEnabled && Username.Trim().Length > 0)
        {
            var include = await page.DisplayAlert(loc["showQr"], loc["proxyQrIncludePass"], loc["proxyQrInclude"], loc["proxyQrWithout"]);
            if (include)
            {
                user = Username.Trim();
                pass = Password;
            }
        }

        var payload = ProxyQrPayload.Build(addresses, server.Port, DiscoveryService.DeviceName(), user, pass);
        AppLog.Write(Tag, $"showing proxy qr (password included: {pass != null})");
        try
        {
            await page.Navigation.PushModalAsync(new QrShowPage(payload, string.Join("  |  ", Addresses)));
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "show qr failed", ex);
        }
    }

    [RelayCommand]
    async Task ScanReceiverQrAsync()
    {
        if (!qr.IsSupported || IsRunning) return;
        var loc = Loc.Instance;
        string? text;
        try
        {
            text = await qr.ScanAsync();
        }
        catch (PermissionException ex)
        {
            AppLog.Error(Tag, "camera permission denied", ex);
            ErrorText = loc["qrNoCamera"];
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "scan failed", ex);
            ErrorText = loc["qrFailed"];
            return;
        }
        if (text == null) return;

        if (!ReverseQrPayload.TryParse(text, out var info))
        {
            AppLog.Write(Tag, "scanned a QR that is not a receiver code");
            ErrorText = loc["pcQrInvalid"];
            return;
        }

        ErrorText = "";
        var candidates = tcp.OrderCandidates(info.Addresses).ToList();
        if (candidates.Count == 0) candidates = info.Addresses.ToList();
        var chosen = candidates[0];
        if (candidates.Count > 1)
        {
            var tests = await Task.WhenAll(candidates.Select(async c =>
            {
                try
                {
                    using var client = new TcpClient();
                    using var cts = new CancellationTokenSource(2000);
                    await client.ConnectAsync(c, info.Port, cts.Token);
                    return c;
                }
                catch
                {
                    return null;
                }
            }));
            chosen = candidates.FirstOrDefault(c => tests.Contains(c)) ?? chosen;
        }
        ReverseHost = chosen;
        ReversePortText = info.Port.ToString(CultureInfo.InvariantCulture);
        PairPassword = info.Password ?? "";
        AppLog.Write(Tag, $"receiver qr: '{info.Name}' addresses=[{string.Join(", ", info.Addresses)}] chosen={chosen}:{info.Port} (password in qr: {info.Password != null})");
    }

    [RelayCommand]
    void ToggleShowPairPassword() => ShowPairPassword = !ShowPairPassword;

    [RelayCommand]
    async Task ShowHelpAsync()
    {
        var page = Host ?? Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return;
        var loc = Loc.Instance;
        await page.DisplayAlert(loc["helpProxyTitle"], loc["helpProxy"], loc["ok"]);
    }

    // ---------------------------------------------------------------- helpers

    int CurrentPort() => server.IsRunning
        ? server.Port
        : (int.TryParse(Normalize(PortText).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535
            ? p
            : ProxyOptions.DefaultPort);

    // Empty = unlimited (0). False = text is not a positive number.
    static bool TryParseLimit(string? text, int unit, out long bytes)
    {
        bytes = 0;
        var t = Normalize(text ?? "").Trim().Replace(',', '.');
        if (t.Length == 0) return true;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return false;
        var unitBytes = unit == 1 ? 1024.0 * 1024 * 1024 : 1024.0 * 1024;
        var total = value * unitBytes;
        bytes = total >= long.MaxValue ? long.MaxValue : (long)total;
        return bytes > 0;
    }

    // Persian / Arabic digits and separators -> ASCII.
    static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch >= '\u06F0' && ch <= '\u06F9') sb.Append((char)('0' + (ch - '\u06F0')));
            else if (ch >= '\u0660' && ch <= '\u0669') sb.Append((char)('0' + (ch - '\u0660')));
            else if (ch == '\u066B' || ch == '\u060C') sb.Append('.');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public static string FormatBytes(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        var inv = CultureInfo.InvariantCulture;
        if (bytes < kb) return bytes.ToString(inv) + " B";
        if (bytes < mb) return (bytes / kb).ToString("0.#", inv) + " KB";
        if (bytes < gb) return (bytes / mb).ToString("0.#", inv) + " MB";
        return (bytes / gb).ToString("0.##", inv) + " GB";
    }
}
