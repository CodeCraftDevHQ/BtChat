using System.Runtime.Versioning;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;

namespace BtChat;

// Fingerprint / face unlock through the system's own prompt (Android 10 and newer).
public sealed class AndroidBiometricAuth : IBiometricAuth
{
    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return false;
            try
            {
                var context = Android.App.Application.Context;
                // 0 = BIOMETRIC_SUCCESS: sensor present and a fingerprint/face is enrolled.
                return context.GetSystemService(Context.BiometricService) is BiometricManager manager
                    && Convert.ToInt32(manager.CanAuthenticate()) == 0;
            }
            catch (Exception ex)
            {
                AppLog.Error("LOCK", "reading biometric state failed", ex);
                return false;
            }
        }
    }

    public Task<bool> AuthenticateAsync(string title, string cancelText)
    {
        var done = new TaskCompletionSource<bool>();
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            done.TrySetResult(false);
            return done.Task;
        }
        try
        {
            Prompt(title, cancelText, done);
        }
        catch (Exception ex)
        {
            AppLog.Error("LOCK", "showing the biometric prompt failed", ex);
            done.TrySetResult(false);
        }
        return done.Task;
    }

    [SupportedOSPlatform("android29.0")]
    static void Prompt(string title, string cancelText, TaskCompletionSource<bool> done)
    {
        var context = (Context?)Platform.CurrentActivity ?? Android.App.Application.Context;
        var executor = context.MainExecutor!;
        var builder = new BiometricPrompt.Builder(context);
        builder.SetTitle(title);
        builder.SetNegativeButton(cancelText, executor, new CancelListener(done));
        var prompt = builder.Build()!;
        prompt.Authenticate(new CancellationSignal(), executor, new Callback(done));
    }

    sealed class Callback(TaskCompletionSource<bool> done) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) => done.TrySetResult(true);

        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) => done.TrySetResult(false);
    }

    sealed class CancelListener(TaskCompletionSource<bool> done) : Java.Lang.Object, IDialogInterfaceOnClickListener
    {
        public void OnClick(IDialogInterface? dialog, int which) => done.TrySetResult(false);
    }
}
