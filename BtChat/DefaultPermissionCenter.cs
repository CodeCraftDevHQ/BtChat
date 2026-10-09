#if !ANDROID
#if WINDOWS
using Windows.Devices.Enumeration;
#endif
namespace BtChat;

// Windows: microphone and camera are controlled by Windows privacy settings; Bluetooth and network need nothing.
public sealed class DefaultPermissionCenter : IPermissionCenter
{
    public Task<IReadOnlyList<PermissionRow>> GetRowsAsync()
    {
        var rows = new List<PermissionRow>();
#if WINDOWS
        rows.Add(new PermissionRow(PermissionKind.Microphone, Allowed(DeviceClass.AudioCapture), true));
        rows.Add(new PermissionRow(PermissionKind.Camera, Allowed(DeviceClass.VideoCapture), true));
#endif
        rows.Add(new PermissionRow(PermissionKind.Bluetooth, true, false));
        rows.Add(new PermissionRow(PermissionKind.Network, true, false));
        return Task.FromResult<IReadOnlyList<PermissionRow>>(rows);
    }

#if WINDOWS
    static bool Allowed(DeviceClass deviceClass)
    {
        try
        {
            var status = DeviceAccessInformation.CreateFromDeviceClass(deviceClass).CurrentStatus;
            return status != DeviceAccessStatus.DeniedByUser && status != DeviceAccessStatus.DeniedBySystem;
        }
        catch (Exception ex)
        {
            AppLog.Error("PERM", "reading device access failed", ex);
            return true;
        }
    }
#endif

    public async Task SetAsync(PermissionKind kind, bool enable)
    {
#if WINDOWS
        var uri = kind switch
        {
            PermissionKind.Microphone => "ms-settings:privacy-microphone",
            PermissionKind.Camera => "ms-settings:privacy-webcam",
            _ => null
        };
        if (uri == null) return;
        try
        {
            await Launcher.Default.OpenAsync(new Uri(uri));
        }
        catch (Exception ex)
        {
            AppLog.Error("PERM", "opening Windows privacy settings failed", ex);
        }
#else
        await Task.CompletedTask;
#endif
    }
}
#endif
