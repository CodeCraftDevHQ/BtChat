using Android.Content;
using Android.Provider;

namespace BtChat;

// Android apps cannot set the Wi-Fi proxy; the best we can do is open the Wi-Fi settings screen.
public sealed class AndroidSystemProxy : ISystemProxy
{
    public bool CanApply => false;
    public bool IsApplied => false;
    public void Apply(string host, int port) => throw new NotSupportedException();
    public void Restore() { }
    public bool CanOpenNetworkSettings => true;

    public void OpenNetworkSettings()
    {
        try
        {
            var intent = new Intent(Settings.ActionWifiSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            AppLog.Error("SYSPROXY", "cannot open Wi-Fi settings", ex);
        }
    }
}
