using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace BtChat
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        public const int PickFilesRequest = 7411;
        public static TaskCompletionSource<Intent?>? PickResult;

#pragma warning disable CA1422
        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == PickFilesRequest)
                PickResult?.TrySetResult(resultCode == Result.Ok ? data : null);
        }
#pragma warning restore CA1422
    }
}
