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
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, Array.Empty<ProxyClientInfo>());

    readonly TcpTransport tcp;
    readonly ProxyServer server = new();
    ProxySnapshot snapshot = Empty;
    string clientSignature = "";

    // Set by the page while it is open, so dialogs and the QR page appear on top of it.
    public Page? Host { get; set; }

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

    public ProxyViewModel(TcpTransport tcp)
    {
        this.tcp = tcp;
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
    public Color DotColor => IsRunning ? (IsLimitReached ? Orange : Green) : Colors.Gray;

    // Short line for the side drawer.
    public string StatusLine => IsRunning
        ? string.Format(Loc.Instance["proxyStatusRunning"], server.Port, snapshot.ActiveClients)
        : Loc.Instance["proxyDrawerHint"];

    public string NotificationText => string.Format(Loc.Instance["proxyNotif"], server.Port);

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
        if (!int.TryParse(Normalize(PortText).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port < 1 || port > 65535)
        {
            ErrorText = loc["proxyBadPort"];
            return;
        }
        string? user = null, pass = null;
        if (AuthEnabled)
        {
            user = Username.Trim();
            pass = Password;
            if (user.Length == 0 || pass.Length == 0) { ErrorText = loc["proxyNeedCreds"]; return; }
            if (user.Contains(':')) { ErrorText = loc["proxyBadUser"]; return; }
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
        RefreshAddresses();
        Apply(server.GetSnapshot());
        RunningChanged?.Invoke();
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
