using Android.App;
using Android.Content;
using AndroidX.Core.App;

namespace BtChat;

public sealed class AndroidMessageAlert : IMessageAlert
{
    const string ChannelId = "btchat_message";
    const int FirstId = 5000;
    const int MaxLines = 5;

    static readonly object gate = new();
    // The last few messages of every chat that has a notification on screen.
    static readonly Dictionary<string, List<string>> lines = new();
    static readonly Dictionary<string, int> ids = new();

    public void Show(string chatId, string title, string text)
    {
        // Nothing to announce while the user is looking at the app.
        if (MainActivity.InForeground) return;
        try
        {
            var context = Android.App.Application.Context;
            var manager = NotificationManagerCompat.From(context);
            if (manager == null || !manager.AreNotificationsEnabled())
            {
                AppLog.Write("NOTIFY", "notifications are off, message not announced");
                return;
            }
            EnsureChannel(context);
            List<string> recent;
            int id;
            lock (gate)
            {
                if (!ids.TryGetValue(chatId, out id))
                {
                    id = FirstId + ids.Count;
                    ids[chatId] = id;
                }
                if (!lines.TryGetValue(chatId, out recent!))
                {
                    recent = new List<string>();
                    lines[chatId] = recent;
                }
                recent.Add(text);
                if (recent.Count > MaxLines) recent.RemoveAt(0);
            }
            PendingIntent? open = null;
            var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
            if (launch != null)
            {
                launch.SetFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
                open = PendingIntent.GetActivity(context, id, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            }
            var builder = new NotificationCompat.Builder(context, ChannelId)
                .SetContentTitle(title)!
                .SetContentText(text)!
                .SetSmallIcon(Android.Resource.Drawable.SymActionChat)!
                .SetPriority(NotificationCompat.PriorityHigh)!
                .SetCategory(NotificationCompat.CategoryMessage)!
                .SetAutoCancel(true)!
                .SetOnlyAlertOnce(false)!
                .SetContentIntent(open)!;
            if (recent.Count > 1)
            {
                var style = new NotificationCompat.InboxStyle();
                string[] snapshot;
                lock (gate) snapshot = recent.ToArray();
                foreach (var line in snapshot) style.AddLine(line);
                builder.SetStyle(style);
                builder.SetNumber(snapshot.Length);
            }
            manager.Notify(id, builder.Build()!);
        }
        catch (Exception ex)
        {
            AppLog.Error("NOTIFY", "showing a message notification failed", ex);
        }
    }

    public void ClearAll() => ClearShown();

    // Also called when the app comes to the front.
    public static void ClearShown()
    {
        try
        {
            int[] shown;
            lock (gate)
            {
                shown = ids.Values.ToArray();
                lines.Clear();
                ids.Clear();
            }
            if (shown.Length == 0) return;
            var manager = NotificationManagerCompat.From(Android.App.Application.Context);
            foreach (var id in shown) manager?.Cancel(id);
        }
        catch (Exception ex)
        {
            AppLog.Error("NOTIFY", "clearing message notifications failed", ex);
        }
    }

    static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
        if (manager.GetNotificationChannel(ChannelId) != null) return;
        // Default sound and vibration; the user can change them in the system's notification settings.
        var channel = new NotificationChannel(ChannelId, Loc.Instance["messageChannel"], NotificationImportance.High);
        channel.EnableVibration(true);
        manager.CreateNotificationChannel(channel);
    }
}
