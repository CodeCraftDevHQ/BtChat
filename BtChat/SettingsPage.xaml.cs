namespace BtChat;

public partial class SettingsPage : ContentPage
{
    readonly MainViewModel vm;
    readonly IPermissionCenter? permissionCenter = IPlatformApplication.Current?.Services.GetService<IPermissionCenter>();
    bool refreshingPermissions;

    public System.Collections.ObjectModel.ObservableCollection<PermissionItem> PermissionItems { get; } = new();

    public SettingsPage(MainViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        BindingContext = vm;
        foreach (var seconds in AppLock.DelayChoices) LockDelayPicker.Items.Add(Loc.Instance["lockD" + seconds]);
        UpdateLockUi();
        VersionLabel.Text = $"BtChat · {Loc.Instance["version"]} {AppInfo.Current.VersionString}";
    }

    // ---- App lock

    bool updatingLockUi;

    void UpdateLockUi()
    {
        updatingLockUi = true;
        try
        {
            var enabled = AppLock.Enabled;
            LockSwitch.IsToggled = enabled;
            LockOptions.IsVisible = enabled;
            var index = Array.IndexOf(AppLock.DelayChoices, AppLock.DelaySeconds);
            LockDelayPicker.SelectedIndex = index >= 0 ? index : Array.IndexOf(AppLock.DelayChoices, AppLock.DefaultDelaySeconds);
            LockBioRow.IsVisible = AppLock.BiometricAvailable;
            LockBioSwitch.IsToggled = AppLock.BiometricEnabled;
        }
        finally
        {
            updatingLockUi = false;
        }
    }

    async void OnLockToggled(object? sender, ToggledEventArgs e)
    {
        if (updatingLockUi || e.Value == AppLock.Enabled) return;
        try
        {
            if (e.Value)
            {
                if (await AppLock.SetupPinAsync(this)) AppLock.Enable();
            }
            else if (await AppLock.ConfirmPinAsync(this, Loc.Instance["lockTurnOffTitle"]))
            {
                AppLock.Disable();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("LOCK", "changing the app lock failed", ex);
        }
        UpdateLockUi();
    }

    void OnLockDelayChanged(object? sender, EventArgs e)
    {
        if (updatingLockUi || LockDelayPicker.SelectedIndex < 0) return;
        AppLock.DelaySeconds = AppLock.DelayChoices[LockDelayPicker.SelectedIndex];
    }

    async void OnLockBioToggled(object? sender, ToggledEventArgs e)
    {
        if (updatingLockUi || e.Value == AppLock.BiometricEnabled) return;
        try
        {
            if (e.Value)
            {
                // Check once that it really works before relying on it to unlock the app.
                var biometric = IPlatformApplication.Current?.Services.GetService<IBiometricAuth>();
                var ok = biometric != null && await biometric.AuthenticateAsync(Loc.Instance["lockBioTitle"], Loc.Instance["cancel"]);
                AppLock.BiometricEnabled = ok;
            }
            else
            {
                AppLock.BiometricEnabled = false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("LOCK", "changing fingerprint unlock failed", ex);
        }
        UpdateLockUi();
    }

    async void OnLockChangePinClicked(object? sender, EventArgs e)
    {
        try
        {
            if (await AppLock.ConfirmPinAsync(this, Loc.Instance["lockTurnOffTitle"]))
                await AppLock.SetupPinAsync(this);
        }
        catch (Exception ex)
        {
            AppLog.Error("LOCK", "changing the PIN failed", ex);
        }
    }

    async void OnLockNowClicked(object? sender, EventArgs e)
    {
        try
        {
            await AppLock.LockNowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("LOCK", "locking now failed", ex);
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = RefreshPermissionsAsync();
        vm.RefreshBatteryStatus();
        // Coming back from the system settings: show what the user changed there.
        if (Window != null) Window.Activated += OnWindowActivated;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (Window != null) Window.Activated -= OnWindowActivated;
    }

    void OnWindowActivated(object? sender, EventArgs e)
    {
        _ = RefreshPermissionsAsync();
        vm.RefreshBatteryStatus();
    }

    async Task RefreshPermissionsAsync()
    {
        if (permissionCenter == null || refreshingPermissions) return;
        refreshingPermissions = true;
        try
        {
            var rows = await permissionCenter.GetRowsAsync();
            var sameList = rows.Count == PermissionItems.Count && rows.Select(r => r.Kind).SequenceEqual(PermissionItems.Select(i => i.Kind));
            if (sameList)
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    PermissionItems[i].Granted = rows[i].Granted;
                    PermissionItems[i].Resync();
                }
            }
            else
            {
                PermissionItems.Clear();
                foreach (var row in rows) PermissionItems.Add(new PermissionItem(row));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("PERM", "reading permissions failed", ex);
        }
        finally
        {
            refreshingPermissions = false;
        }
    }

    async void OnPermissionInfoClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: PermissionItem item }) return;
        await DisplayAlert(item.Title, item.Info, Loc.Instance["close"]);
    }

    async void OnPermissionToggled(object? sender, ToggledEventArgs e)
    {
        if (sender is not Switch { BindingContext: PermissionItem item } || permissionCenter == null) return;
        // The switch also fires when a refresh sets it to the real state; only a real user change goes on.
        if (e.Value == item.Granted) return;
        try
        {
            await permissionCenter.SetAsync(item.Kind, e.Value);
        }
        catch (Exception ex)
        {
            AppLog.Error("PERM", $"changing {item.Kind} failed", ex);
        }
        await RefreshPermissionsAsync();
        item.Resync();
    }

    async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();

    const string WindowsDownloadUrl = "https://github.com/CodeCraftDevHQ/BtChat";

    async void OnWindowsDownloadClicked(object? sender, EventArgs e)
    {
        try
        {
            await Launcher.Default.OpenAsync(new Uri(WindowsDownloadUrl));
        }
        catch (Exception ex)
        {
            AppLog.Error("UI", "opening Windows download link failed", ex);
            await DisplayAlert(Loc.Instance["setAbout"], Loc.Instance["openLinkFailed"], Loc.Instance["close"]);
        }
    }

    async void OnGuideClicked(object? sender, EventArgs e)
    {
        try
        {
            await GuidePage.ShowAsync(this);
        }
        catch (Exception ex)
        {
            AppLog.Error("GUIDE", "opening the guide failed", ex);
        }
    }

    async void OnLogClicked(object? sender, EventArgs e)
    {
        await CloseAsync();
        vm.ToggleLogCommand.Execute(null);
    }

    async Task CloseAsync()
    {
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("UI", "closing settings failed", ex);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }
}
