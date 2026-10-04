namespace BtChat;

public sealed class AndroidPermissionGate : IPermissionGate
{
    public Task<bool> EnsureAsync(PermissionKind kind) => MainThread.InvokeOnMainThreadAsync(() => kind switch
    {
        PermissionKind.Bluetooth => OperatingSystem.IsAndroidVersionAtLeast(31) ? Run<BluetoothPermission>(kind) : Task.FromResult(true),
        PermissionKind.Camera => Run<Permissions.Camera>(kind),
        PermissionKind.Microphone => Run<Permissions.Microphone>(kind),
        PermissionKind.CameraCapture => Run<Permissions.Camera>(kind),
        PermissionKind.Notifications => OperatingSystem.IsAndroidVersionAtLeast(33) ? Run<Permissions.PostNotifications>(kind) : Task.FromResult(true),
        PermissionKind.Storage => OperatingSystem.IsAndroidVersionAtLeast(29) ? Task.FromResult(true) : Run<Permissions.StorageWrite>(kind),
        _ => Task.FromResult(true)
    });

    static async Task<bool> Run<T>(PermissionKind kind) where T : Permissions.BasePermission, new()
    {
        var status = await Permissions.CheckStatusAsync<T>();
        if (status == PermissionStatus.Granted) return true;

        var key = "perm." + kind;
        var asked = Preferences.Default.Get(key, false);
        var canAsk = !asked || Permissions.ShouldShowRationale<T>();
        var title = Loc.Instance["perm" + kind + "Title"];
        if (canAsk)
        {
            var go = await Confirm(title, Loc.Instance["perm" + kind + "Why"], Loc.Instance["permContinue"], Loc.Instance["permNotNow"]);
            if (!go)
            {
                AppLog.Write("PERM", $"{kind} rationale declined");
                return false;
            }
            Preferences.Default.Set(key, true);
            status = await Permissions.RequestAsync<T>();
            AppLog.Write("PERM", $"{kind} request result={status}");
            if (status == PermissionStatus.Granted) return true;
            if (Permissions.ShouldShowRationale<T>()) return false;
        }

        var open = await Confirm(title, Loc.Instance["permBlocked"], Loc.Instance["permOpenSettings"], Loc.Instance["cancel"]);
        if (open) AppInfo.Current.ShowSettingsUI();
        return false;
    }

    static async Task<bool> Confirm(string title, string message, string accept, string cancel)
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return false;
        return await page.DisplayAlert(title, message, accept, cancel);
    }
}
