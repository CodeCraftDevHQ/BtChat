using System.Security.Cryptography;

namespace BtChat;

public interface IBiometricAuth
{
    bool IsAvailable { get; }
    Task<bool> AuthenticateAsync(string title, string cancelText);
}

#if !ANDROID
public sealed class NoBiometricAuth : IBiometricAuth
{
    public bool IsAvailable => false;
    public Task<bool> AuthenticateAsync(string title, string cancelText) => Task.FromResult(false);
}
#endif

// PIN lock for the whole app. Off by default; the user turns it on in Settings.
// The PIN is never stored, only a salted PBKDF2 hash of it.
public static class AppLock
{
    const string EnabledKey = "lockEnabled";
    const string DelayKey = "lockDelaySeconds";
    const string BiometricKey = "lockBiometric";
    const string SaltKey = "lockSalt";
    const string HashKey = "lockHash";
    const int MinDigits = 4;
    const int MaxDigits = 8;
    const int Iterations = 100_000;

    public static readonly int[] DelayChoices = { 30, 60, 300, 900, 1800, 3600 };
    public const int DefaultDelaySeconds = 300;

    static bool pageShowing;
    static bool started;
    static bool wasInBackground;
    static DateTime backgroundSince;
    static int failures;
    static DateTime lockedUntil;

    static IBiometricAuth? Biometric => IPlatformApplication.Current?.Services.GetService<IBiometricAuth>();

    public static bool HasPin => Preferences.Default.ContainsKey(HashKey);
    public static bool Enabled => Preferences.Default.Get(EnabledKey, false) && HasPin;
    public static bool BiometricAvailable => Biometric?.IsAvailable == true;

    public static bool BiometricEnabled
    {
        get => Preferences.Default.Get(BiometricKey, false) && BiometricAvailable;
        set => Preferences.Default.Set(BiometricKey, value);
    }

    public static int DelaySeconds
    {
        get => Preferences.Default.Get(DelayKey, DefaultDelaySeconds);
        set => Preferences.Default.Set(DelayKey, value);
    }

    // ---- turning the lock on and off

    public static void Enable() => Preferences.Default.Set(EnabledKey, true);

    public static void Disable()
    {
        Preferences.Default.Set(EnabledKey, false);
        Preferences.Default.Remove(HashKey);
        Preferences.Default.Remove(SaltKey);
        Preferences.Default.Set(BiometricKey, false);
        AppLog.Write("LOCK", "app lock turned off");
    }

    // ---- PIN storage

    static byte[] Hash(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);

    static void SavePin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        Preferences.Default.Set(SaltKey, Convert.ToBase64String(salt));
        Preferences.Default.Set(HashKey, Convert.ToBase64String(Hash(pin, salt)));
    }

    static bool PinMatches(string pin)
    {
        try
        {
            var salt = Convert.FromBase64String(Preferences.Default.Get(SaltKey, ""));
            var expected = Convert.FromBase64String(Preferences.Default.Get(HashKey, ""));
            return CryptographicOperations.FixedTimeEquals(Hash(pin, salt), expected);
        }
        catch (Exception ex)
        {
            AppLog.Error("LOCK", "checking the PIN failed", ex);
            return false;
        }
    }

    static string? FormatProblem(string pin) =>
        pin.Length >= MinDigits && pin.Length <= MaxDigits && pin.All(char.IsAsciiDigit) ? null : Loc.Instance["lockBadFormat"];

    // Returns a message when the PIN is not accepted, null when it is right.
    static string? Check(string pin)
    {
        var now = DateTime.UtcNow;
        if (now < lockedUntil)
            return string.Format(Loc.Instance["lockWait"], (int)Math.Ceiling((lockedUntil - now).TotalSeconds));
        if (PinMatches(pin))
        {
            failures = 0;
            return null;
        }
        failures++;
        if (failures >= 5)
        {
            failures = 0;
            lockedUntil = now.AddSeconds(30);
            AppLog.Write("LOCK", "too many wrong PINs, waiting 30 s");
            return string.Format(Loc.Instance["lockWait"], 30);
        }
        return Loc.Instance["lockWrong"];
    }

    // ---- questions shown from Settings

    // Asks for a new PIN twice and saves it. False when the user cancelled.
    public static async Task<bool> SetupPinAsync(Page host)
    {
        var loc = Loc.Instance;
        var first = await new PinPage(loc["lockNewTitle"], loc["lockNewMsg"], true,
            pin => Task.FromResult(FormatProblem(pin))).ShowAsync(host);
        if (first == null) return false;
        var again = await new PinPage(loc["lockRepeatTitle"], loc["lockRepeatMsg"], true,
            pin => Task.FromResult(pin == first ? null : loc["lockMismatch"])).ShowAsync(host);
        if (again == null) return false;
        SavePin(first);
        AppLog.Write("LOCK", "PIN saved");
        return true;
    }

    // Asks for the current PIN. True when it was entered correctly.
    public static async Task<bool> ConfirmPinAsync(Page host, string title)
    {
        var loc = Loc.Instance;
        var entered = await new PinPage(title, loc["lockMsg"], true, pin => Task.FromResult(Check(pin))).ShowAsync(host);
        return entered != null;
    }

    // ---- locking and unlocking

    public static Task LockNowAsync() => ShowLockAsync();

    // Called when the window comes to the front / goes away (see App.xaml.cs).
    public static void OnBackground()
    {
        if (pageShowing) return;
        wasInBackground = true;
        backgroundSince = DateTime.UtcNow;
    }

    public static void OnForeground()
    {
        var firstTime = !started;
        started = true;
        if (!Enabled) return;
        if (firstTime)
        {
            // First time the app comes up: it always starts locked.
            _ = ShowLockAsync();
            return;
        }
        if (pageShowing || !wasInBackground) return;
        wasInBackground = false;
        if ((DateTime.UtcNow - backgroundSince).TotalSeconds >= DelaySeconds) _ = ShowLockAsync();
    }

    static async Task ShowLockAsync()
    {
        if (pageShowing || !Enabled) return;
        pageShowing = true;
        try
        {
            // The window may not be ready to show a page yet right after start-up.
            await Task.Delay(300);
            var host = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (host == null) return;
            var loc = Loc.Instance;
            var biometric = Biometric;
            Func<Task<bool>>? useFingerprint = BiometricEnabled && biometric != null
                ? () => biometric.AuthenticateAsync(loc["lockBioTitle"], loc["lockUsePin"])
                : null;
            AppLog.Write("LOCK", "app locked");
            await new PinPage(loc["lockTitle"], loc["lockMsg"], false, pin => Task.FromResult(Check(pin)), useFingerprint).ShowAsync(host);
            AppLog.Write("LOCK", "app unlocked");
        }
        catch (Exception ex)
        {
            AppLog.Error("LOCK", "showing the lock screen failed", ex);
        }
        finally
        {
            pageShowing = false;
            wasInBackground = false;
        }
    }
}
