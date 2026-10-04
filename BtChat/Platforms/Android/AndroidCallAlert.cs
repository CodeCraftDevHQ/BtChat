using Android.App;
using Android.Content;
using Android.Media;
using AndroidX.Core.App;

namespace BtChat;

public sealed class AndroidCallAlert : ICallAlert
{
    const string ChannelId = "btchat_call";
    const int NotificationId = 4712;

    Ringtone? ringtone;

    public void StartRinging(string name)
    {
        StopRinging();
        var context = Android.App.Application.Context;
        try
        {
            if (MainActivity.InForeground)
            {
                var uri = RingtoneManager.GetDefaultUri(RingtoneType.Ringtone);
                ringtone = RingtoneManager.GetRingtone(context, uri);
                if (ringtone != null)
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(28)) ringtone.Looping = true;
                    ringtone.Play();
                }
            }
            else
            {
                ShowNotification(context, name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("CALL", "ringing failed", ex);
        }
    }

    public void StopRinging()
    {
        try
        {
            ringtone?.Stop();
        }
        catch
        {
        }
        ringtone = null;
        try
        {
            NotificationManagerCompat.From(Android.App.Application.Context)?.Cancel(NotificationId);
        }
        catch
        {
        }
    }

    static void ShowNotification(Context context, string name)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && context.GetSystemService(Context.NotificationService) is NotificationManager manager
            && manager.GetNotificationChannel(ChannelId) == null)
        {
            var channel = new NotificationChannel(ChannelId, Loc.Instance["callChannel"], NotificationImportance.High);
            channel.EnableVibration(true);
            channel.SetSound(RingtoneManager.GetDefaultUri(RingtoneType.Ringtone),
                new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.NotificationRingtone)!.Build());
            manager.CreateNotificationChannel(channel);
        }
        PendingIntent? open = null;
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch != null)
        {
            launch.SetFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
            open = PendingIntent.GetActivity(context, 1, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        }
        var notification = new NotificationCompat.Builder(context, ChannelId)
            .SetContentTitle(Loc.Instance["callIncoming"])!
            .SetContentText(name)!
            .SetSmallIcon(Android.Resource.Drawable.SymActionCall)!
            .SetPriority(NotificationCompat.PriorityHigh)!
            .SetCategory(NotificationCompat.CategoryCall)!
            .SetAutoCancel(true)!
            .SetTimeoutAfter(45000)!
            .SetContentIntent(open)!
            .Build()!;
        NotificationManagerCompat.From(context)?.Notify(NotificationId, notification);
    }
}
