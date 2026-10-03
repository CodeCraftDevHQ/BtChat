namespace BtChat;

public enum PermissionKind
{
    Bluetooth,
    Camera,
    Notifications,
    Storage
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
