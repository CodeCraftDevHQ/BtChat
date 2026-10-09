namespace BtChat;

public enum PermissionKind
{
    Bluetooth,
    Camera,
    Notifications,
    Storage,
    Microphone,
    CameraCapture,
    CallMicrophone,
    CallCamera,
    // Only shown in Settings > Permissions (nothing is asked at run time):
    Network,
    Background
}

public interface IPermissionGate
{
    Task<bool> EnsureAsync(PermissionKind kind);
}

#if !ANDROID
public sealed class DefaultPermissionGate : IPermissionGate
{
    public Task<bool> EnsureAsync(PermissionKind kind) => Task.FromResult(true);
}
#endif
