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
            rows.Add(new PermissionRow(PermissionKind.Network, true, false));
            rows.Add(new PermissionRow(PermissionKind.Background, true, false));
            return rows;
        });

    static async Task<bool> IsGranted<T>() where T : Permissions.BasePermission, new() =>
        await Permissions.CheckStatusAsync<T>() == PermissionStatus.Granted;

    public async Task SetAsync(PermissionKind kind, bool enable)
    {
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
