using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace BtChat;

public sealed record SysProxyPrev(int? Enable, string? Server, string? Override, string? Pac);

// Sets / restores the per-user Windows proxy (the one browsers and most apps read).
public sealed class WindowsSystemProxy : ISystemProxy
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    const string PrefKey = "sysProxyPrev";
    const int OptionRefresh = 37;
    const int OptionSettingsChanged = 39;
    static bool exitHooked;

    [DllImport("wininet.dll", SetLastError = true)]
    static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);

    public bool CanApply => true;
    public bool IsApplied => Preferences.Default.ContainsKey(PrefKey);
    public bool CanOpenNetworkSettings => false;
    public void OpenNetworkSettings() { }

    public void Apply(string host, int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true)
                        ?? throw new InvalidOperationException("Internet Settings key not found");
        if (!IsApplied)
        {
            // remember what the user had, but only the first time (re-applying must not overwrite it)
            var prev = new SysProxyPrev(
                key.GetValue("ProxyEnable") as int?,
                key.GetValue("ProxyServer") as string,
                key.GetValue("ProxyOverride") as string,
                key.GetValue("AutoConfigURL") as string);
            Preferences.Default.Set(PrefKey, JsonSerializer.Serialize(prev));
        }
        key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        if (string.IsNullOrEmpty(key.GetValue("ProxyOverride") as string))
            key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        key.DeleteValue("AutoConfigURL", false);
        Notify();
        HookExit();
        AppLog.Write("SYSPROXY", $"Windows proxy set to {host}:{port}");
    }

    public void Restore()
    {
        var json = Preferences.Default.Get(PrefKey, "");
        if (json.Length == 0) return;
        try
        {
            SysProxyPrev? prev = null;
            try { prev = JsonSerializer.Deserialize<SysProxyPrev>(json); } catch { }
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true);
            if (key != null)
            {
                if (prev == null)
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                }
                else
                {
                    if (prev.Enable is int enable) key.SetValue("ProxyEnable", enable, RegistryValueKind.DWord);
                    else key.DeleteValue("ProxyEnable", false);
                    SetOrDelete(key, "ProxyServer", prev.Server);
                    SetOrDelete(key, "ProxyOverride", prev.Override);
                    SetOrDelete(key, "AutoConfigURL", prev.Pac);
                }
            }
            Notify();
            AppLog.Write("SYSPROXY", "Windows proxy settings restored");
        }
        catch (Exception ex)
        {
            AppLog.Error("SYSPROXY", "restore failed", ex);
        }
        finally
        {
            Preferences.Default.Remove(PrefKey);
        }
    }

    static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value == null) key.DeleteValue(name, false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    static void Notify()
    {
        InternetSetOption(IntPtr.Zero, OptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, OptionRefresh, IntPtr.Zero, 0);
    }

    // If the app is closed normally the old settings come back; after a crash the next start restores them.
    void HookExit()
    {
        if (exitHooked) return;
        exitHooked = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Restore(); } catch { }
        };
    }
}
