using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace BtChat
{
    // BtChat shows up in the system share menu for any kind of file.
    [IntentFilter(new[] { Intent.ActionSend, Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // A recreated activity gets its old intent again: only a fresh start counts as a new share.
            if (savedInstanceState == null) HandleShare(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            if (intent == null) return;
            Intent = intent;
            HandleShare(intent);
        }

        static void HandleShare(Intent? intent)
        {
            if (intent == null) return;
            if (intent.Action != Intent.ActionSend && intent.Action != Intent.ActionSendMultiple) return;
            if ((intent.Flags & ActivityFlags.LaunchedFromHistory) != 0) return;
            try
            {
                var items = ShareReader.Read(intent);
                AppLog.Write("SHARE", $"share intent received: {items.Count} file(s)");
                if (items.Count > 0) ShareInbox.Push(items);
            }
            catch (Exception ex)
            {
                AppLog.Error("SHARE", "reading the share intent failed", ex);
            }
        }

        public static volatile bool InForeground;

        protected override void OnResume()
        {
            base.OnResume();
            InForeground = true;
        }

        protected override void OnPause()
        {
            InForeground = false;
            base.OnPause();
        }

        public const int PickFilesRequest = 7411;
        public const int EnableBluetoothRequest = 7412;
        public static TaskCompletionSource<Intent?>? PickResult;
        public static TaskCompletionSource<bool>? EnableBluetoothResult;

#pragma warning disable CA1422
        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == PickFilesRequest)
                PickResult?.TrySetResult(resultCode == Result.Ok ? data : null);
            else if (requestCode == EnableBluetoothRequest)
                EnableBluetoothResult?.TrySetResult(resultCode == Result.Ok);
        }
#pragma warning restore CA1422
    }
}
