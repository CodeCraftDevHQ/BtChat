using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;
using BtChat.Tun;

namespace BtChat;

public sealed record VpnConfig(string Host, int Port, string? User, string? Password);

[Service(Name = "com.companyname.btchat.BtVpnService", Permission = "android.permission.BIND_VPN_SERVICE", Exported = true)]
[IntentFilter(new[] { "android.net.VpnService" })]
public class BtVpnService : VpnService
{
    const string Tag = "TUN";
    const string ChannelId = "btchat_vpn";
    const int NotificationId = 4712;
    public const string StopAction = "com.companyname.btchat.VPN_STOP";

    static readonly object sync = new();

    public static VpnConfig? Pending { get; set; }
    public static TaskCompletionSource<string?>? StartCompleted { get; set; }
    public static BtVpnService? Current { get; private set; }
    public static string? LastError { get; private set; }
    public static event Action? Changed;

    public static bool IsRunning => Current?.engine != null;
    public static long BytesDown => Current?.engine?.BytesDown ?? 0;
    public static long BytesUp => Current?.engine?.BytesUp ?? 0;
    public static int ActiveFlows => Current?.engine?.ActiveFlows ?? 0;

    ParcelFileDescriptor? tun;
    TunEngine? engine;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction)
        {
            StopTunnel(null);
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        var config = Pending;
        if (config == null)
        {
            StartCompleted?.TrySetResult("no configuration");
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        StartTunnel(config);
        return StartCommandResult.NotSticky;
    }

    void StartTunnel(VpnConfig config)
    {
        lock (sync)
        {
            if (engine != null) StopTunnelLocked(null);
            try
            {
                var builder = new Builder(this);
                builder.SetSession("BtChat");
                builder.SetMtu(1400);
                builder.AddAddress("198.18.0.1", 32);
                builder.AddRoute("0.0.0.0", 0);
                builder.AddDnsServer("198.18.0.2");
                try
                {
                    builder.AddAddress("fd66:6274:6368:6174::1", 128);
                    builder.AddRoute("::", 0);
                }
                catch (Exception ex)
                {
                    AppLog.Error(Tag, "ipv6 route not added", ex);
                }
                try
                {
                    builder.AddDisallowedApplication(PackageName!);
                }
                catch (Exception ex)
                {
                    AppLog.Error(Tag, "cannot exclude BtChat from the tunnel", ex);
                }
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                {
                    try
                    {
                        builder.ExcludeRoute(new IpPrefix(Java.Net.InetAddress.GetByName("10.0.0.0")!, 8));
                        builder.ExcludeRoute(new IpPrefix(Java.Net.InetAddress.GetByName("172.16.0.0")!, 12));
                        builder.ExcludeRoute(new IpPrefix(Java.Net.InetAddress.GetByName("192.168.0.0")!, 16));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error(Tag, "local routes not excluded", ex);
                    }
                }
                if (OperatingSystem.IsAndroidVersionAtLeast(29)) builder.SetMetered(false);

                var pfd = builder.Establish();
                if (pfd == null) throw new InvalidOperationException("the system refused to create the tunnel interface");
                tun = pfd;

                var options = new TunOptions
                {
                    ProxyHost = config.Host,
                    ProxyPort = config.Port,
                    User = config.User,
                    Password = config.Password,
                    Mtu = 1400
                };
                var created = new TunEngine(new AndroidTunIo(pfd.FileDescriptor!), options);
                created.Stopped += reason =>
                {
                    AppLog.Write(Tag, "engine stopped: " + reason);
                    StopTunnel(reason);
                    StopSelf();
                };
                engine = created;
                Current = this;
                LastError = null;
                created.Start();
                ShowNotification(config);
                AppLog.Write(Tag, $"started via {config.Host}:{config.Port}");
                StartCompleted?.TrySetResult(null);
            }
            catch (Exception ex)
            {
                AppLog.Error(Tag, "start failed", ex);
                LastError = ex.Message;
                StopTunnelLocked(ex.Message);
                StartCompleted?.TrySetResult(ex.Message);
                StopSelf();
            }
        }
        Changed?.Invoke();
    }

    void StopTunnel(string? error)
    {
        lock (sync) StopTunnelLocked(error);
        Changed?.Invoke();
    }

    void StopTunnelLocked(string? error)
    {
        var e = engine;
        var t = tun;
        engine = null;
        tun = null;
        if (error != null) LastError = error;
        try { e?.Dispose(); } catch (Exception ex) { AppLog.Error(Tag, "engine dispose failed", ex); }
        try { t?.Close(); } catch (Exception ex) { AppLog.Error(Tag, "tun close failed", ex); }
        try
        {
            (GetSystemService(NotificationService) as NotificationManager)?.Cancel(NotificationId);
        }
        catch
        {
        }
        if (Current == this) Current = null;
        AppLog.Write(Tag, "stopped");
    }

    public override void OnRevoke()
    {
        AppLog.Write(Tag, "revoked by the system");
        StopTunnel(null);
        StopSelf();
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        StopTunnel(null);
        base.OnDestroy();
    }

    void ShowNotification(VpnConfig config)
    {
        try
        {
            var manager = GetSystemService(NotificationService) as NotificationManager;
            if (manager == null) return;
            if (OperatingSystem.IsAndroidVersionAtLeast(26) && manager.GetNotificationChannel(ChannelId) == null)
            {
                var channel = new NotificationChannel(ChannelId, Loc.Instance["vpnNotifChannel"], NotificationImportance.Low);
                channel.SetShowBadge(false);
                manager.CreateNotificationChannel(channel);
            }
            PendingIntent? open = null;
            var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
            if (launch != null)
            {
                launch.SetFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
                open = PendingIntent.GetActivity(this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            }
            var stop = new Intent(this, typeof(BtVpnService)).SetAction(StopAction);
            var stopPending = PendingIntent.GetService(this, 1, stop, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var notification = new NotificationCompat.Builder(this, ChannelId)
                .SetContentTitle(Loc.Instance["vpnNotifTitle"])!
                .SetContentText($"{config.Host}:{config.Port}")!
                .SetSmallIcon(Android.Resource.Drawable.IcLockLock)!
                .SetOngoing(true)!
                .SetOnlyAlertOnce(true)!
                .SetContentIntent(open)!
                .AddAction(Android.Resource.Drawable.IcMenuCloseClearCancel, Loc.Instance["vpnNotifStop"], stopPending)!
                .Build()!;
            manager.Notify(NotificationId, notification);
        }
        catch (Exception ex)
        {
            AppLog.Error(Tag, "notification failed", ex);
        }
    }
}
