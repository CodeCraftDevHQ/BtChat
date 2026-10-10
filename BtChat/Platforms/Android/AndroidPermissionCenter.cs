namespace BtChat;

public sealed class AndroidPermissionCenter(IPermissionGate gate) : IPermissionCenter
{
    public Task<IReadOnlyList<PermissionRow>> GetRowsAsync() =>
        MainThread.InvokeOnMainThreadAsync<IReadOnlyList<PermissionRow>>(async () =>
        {
            var rows = new List<PermissionRow>();
            rows.Add(OperatingSystem.IsAndroidVersionAtLeast(31)
                ? new PermissionRow(PermissionKind.Bluetooth, await IsGranted<BluetoothPermission>(), true)
                : new PermissionRow(PermissionKind.Bluetooth, true, false));
            rows.Add(new PermissionRow(PermissionKind.Camera, await IsGranted<Permissions.Camera>(), true));
            rows.Add(new PermissionRow(PermissionKind.Microphone, await IsGranted<Permissions.Microphone>(), true));
            rows.Add(OperatingSystem.IsAndroidVersionAtLeast(33)
                ? new PermissionRow(PermissionKind.Notifications, await IsGranted<Permissions.PostNotifications>(), true)
                : new PermissionRow(PermissionKind.Notifications, true, false));
            if (!OperatingSystem.IsAndroidVersionAtLeast(29))
                rows.Add(new PermissionRow(PermissionKind.Storage, await IsGranted<Permissions.StorageWrite>(), true));
            rows.Add(new PermissionRow(PermissionKind.Network, DataIsUnrestricted(), true));
            rows.Add(new PermissionRow(PermissionKind.Background, true, false));
            return rows;
        });

    // Internet itself is granted at install; what the user can still restrict is the data saver (background data).
    static bool DataIsUnrestricted()
    {
        try
        {
            var manager = (Android.Net.ConnectivityManager?)Android.App.Application.Context.GetSystemService(Android.Content.Context.ConnectivityService);
            return manager == null || manager.RestrictBackgroundStatus != Android.Net.RestrictBackgroundStatus.Enabled;
        }
        catch (Exception ex)
        {
            AppLog.Error("PERM", "reading data saver state failed", ex);
            return true;
        }
    }

    static void OpenDataSettings()
    {
        var context = Android.App.Application.Context;
        try
        {
            var intent = new Android.Content.Intent("android.settings.IGNORE_BACKGROUND_DATA_RESTRICTIONS_SETTINGS",
                Android.Net.Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            AppLog.Error("PERM", "opening data saver settings failed, opening app settings", ex);
            AppInfo.Current.ShowSettingsUI();
        }
    }

    static async Task<bool> IsGranted<T>() where T : Permissions.BasePermission, new() =>
        await Permissions.CheckStatusAsync<T>() == PermissionStatus.Granted;

    public async Task SetAsync(PermissionKind kind, bool enable)
    {
        if (kind == PermissionKind.Network)
        {
            OpenDataSettings();
            return;
        }
        if (enable)
        {
            // The gate explains why the permission is needed, asks, and sends the user to the settings when it was refused for good.
            await gate.EnsureAsync(kind);
            return;
        }
        AppLog.Write("PERM", $"{kind} turn off -> system settings");
        AppInfo.Current.ShowSettingsUI();
    }
}
