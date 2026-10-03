using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using AndroidX.Core.App;

namespace BtChat;

// Foreground service: while it runs Android keeps the process (and the open sockets) alive in the background.
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public class ConnectionService : Service
{
    public const string ChannelId = "btchat_connection";
    public const string TextExtra = "text";
    const int NotificationId = 4711;

    public static event Action? TaskRemoved;

    PowerManager.WakeLock? wakeLock;
    WifiManager.WifiLock? wifiLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var text = intent?.GetStringExtra(TextExtra) ?? "BtChat";
        try
        {
            EnsureChannel();
            var notification = BuildNotification(text);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                StartForeground(NotificationId, notification, ForegroundService.TypeConnectedDevice);
            else
                StartForeground(NotificationId, notification);
            AcquireLocks();
            AppLog.Write("KEEPALIVE", $"foreground service running: {text}");
        }
        catch (Exception ex)
        {
            AppLog.Error("KEEPALIVE", "cannot start foreground", ex);
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        AppLog.Write("KEEPALIVE", "app removed from recent apps");
        try { TaskRemoved?.Invoke(); } catch (Exception ex) { AppLog.Error("KEEPALIVE", "exit handler failed", ex); }
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        ReleaseLocks();
        AppLog.Write("KEEPALIVE", "foreground service destroyed");
        base.OnDestroy();
    }

    void EnsureChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = GetSystemService(NotificationService) as NotificationManager;
        if (manager?.GetNotificationChannel(ChannelId) != null) return;
        var channel = new NotificationChannel(ChannelId, Loc.Instance["notifChannel"], NotificationImportance.Low);
        channel.SetShowBadge(false);
        manager?.CreateNotificationChannel(channel);
    }

    Notification BuildNotification(string text)
    {
        PendingIntent? open = null;
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        if (launch != null)
        {
            launch.SetFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
            open = PendingIntent.GetActivity(this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        }
        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("BtChat")!
            .SetContentText(text)!
            .SetSmallIcon(Android.Resource.Drawable.StatSysDataBluetooth)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetContentIntent(open)!
            .Build()!;
    }

#pragma warning disable CA1422, CS0618
    void AcquireLocks()
    {
        try
        {
            if (wakeLock == null)
            {
                var power = GetSystemService(PowerService) as PowerManager;
                wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "btchat:connection");
                wakeLock?.SetReferenceCounted(false);
            }
            if (wakeLock != null && !wakeLock.IsHeld) wakeLock.Acquire();

            if (wifiLock == null)
            {
                var wifi = GetSystemService(WifiService) as WifiManager;
                wifiLock = wifi?.CreateWifiLock(WifiMode.FullHighPerf, "btchat:connection");
                wifiLock?.SetReferenceCounted(false);
            }
            if (wifiLock != null && !wifiLock.IsHeld) wifiLock.Acquire();
        }
        catch (Exception ex)
        {
            AppLog.Error("KEEPALIVE", "acquire locks failed", ex);
        }
    }
#pragma warning restore CA1422, CS0618

    void ReleaseLocks()
    {
        try
        {
            if (wakeLock != null && wakeLock.IsHeld) wakeLock.Release();
            if (wifiLock != null && wifiLock.IsHeld) wifiLock.Release();
        }
        catch (Exception ex)
        {
            AppLog.Error("KEEPALIVE", "release locks failed", ex);
        }
    }
}
