namespace BtChat;

// Keeps the app (and its connection) alive while it is in the background.
public interface IKeepAlive
{
    // Raised when the user swipes the app away from the recent-apps list.
    event Action? ExitRequested;
    Task EnsurePermissionAsync();
    void Update(bool active, string text);
    // While a call runs the service also declares the microphone, so Android keeps the mic alive in the background.
    void SetInCall(bool inCall);
    bool CanOpenBatterySettings { get; }
    Task OpenBatterySettingsAsync();
}

#if !ANDROID
public sealed class NoKeepAlive : IKeepAlive
{
    public event Action? ExitRequested
    {
        add { }
        remove { }
    }

    public bool CanOpenBatterySettings => false;
    public Task OpenBatterySettingsAsync() => Task.CompletedTask;
    public Task EnsurePermissionAsync() => Task.CompletedTask;
    public void Update(bool active, string text)
    {
    }

    public void SetInCall(bool inCall)
    {
    }
}
#endif
