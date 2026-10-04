using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace BtChat;

public sealed class AndroidKeepAlive : IKeepAlive
{
    readonly IPermissionGate permissions;
    readonly object gate = new();
    bool running;
    string? lastText;
    bool inCall;
    bool lastCall;

    public event Action? ExitRequested;

    public AndroidKeepAlive(IPermissionGate permissions)
    {
        this.permissions = permissions;
        ConnectionService.TaskRemoved += () => ExitRequested?.Invoke();
    }

    public bool CanOpenBatterySettings => true;

    // Opens the "don't restrict this app's battery use" screen; falls back to simpler screens if the phone has none.
    public Task OpenBatterySettingsAsync()
    {
        var context = Android.App.Application.Context;
        var package = context.PackageName!;
        void Start(Intent intent)
        {
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        var appDetails = () => new Intent(Settings.ActionApplicationDetailsSettings, AndroidUri.Parse("package:" + package));
        try
        {
            var power = context.GetSystemService(Context.PowerService) as PowerManager;
            if (power != null && power.IsIgnoringBatteryOptimizations(package))
            {
                AppLog.Write("KEEPALIVE", "battery already unrestricted, opening app details");
                Android.Widget.Toast.MakeText(context, Loc.Instance["batteryAlready"], Android.Widget.ToastLength.Long)?.Show();
                Start(appDetails());
                return Task.CompletedTask;
            }
            try
            {
                Start(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings));
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                AppLog.Error("KEEPALIVE", "battery list unavailable", ex);
            }
            Start(appDetails());
        }
        catch (Exception ex)
        {
            AppLog.Error("KEEPALIVE", "cannot open battery settings", ex);
        }
        return Task.CompletedTask;
    }

    public async Task EnsurePermissionAsync()
    {
        // Android 13+ hides the foreground-service notification until this is granted.
        try
        {
            var granted = await permissions.EnsureAsync(PermissionKind.Notifications);
            AppLog.Write("KEEPALIVE", $"notification permission granted={granted}");
        }
        catch (Exception ex)
        {
            AppLog.Error("KEEPALIVE", "notification permission failed", ex);
        }
    }

    public void SetInCall(bool inCall)
    {
        lock (gate)
        {
            this.inCall = inCall;
            if (running && lastText != null) Update(true, lastText);
        }
    }

    public void Update(bool active, string text)
    {
        lock (gate)
        {
            var context = Android.App.Application.Context;
            try
            {
                if (active)
                {
                    if (running && text == lastText && inCall == lastCall) return;
                    var intent = new Intent(context, typeof(ConnectionService));
                    intent.PutExtra(ConnectionService.TextExtra, text);
                    intent.PutExtra(ConnectionService.CallExtra, inCall);
                    if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
                    else context.StartService(intent);
                    running = true;
                    lastText = text;
                    lastCall = inCall;
                }
                else if (running)
                {
                    context.StopService(new Intent(context, typeof(ConnectionService)));
                    running = false;
                    lastText = null;
                    AppLog.Write("KEEPALIVE", "foreground service stop requested");
                }
            }
            catch (Exception ex)
            {
                // Android 12+ refuses to start it while the app is fully in the background.
                AppLog.Error("KEEPALIVE", "service start/stop failed", ex);
            }
        }
    }
}
