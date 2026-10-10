#if !ANDROID
#if WINDOWS
using Windows.Devices.Enumeration;
#endif
namespace BtChat;

// Windows: microphone and camera are controlled by Windows privacy settings; Bluetooth and network need nothing.
public sealed class DefaultPermissionCenter : IPermissionCenter
{
    public async Task<IReadOnlyList<PermissionRow>> GetRowsAsync()
    {
        var rows = new List<PermissionRow>();
#if WINDOWS
        rows.Add(new PermissionRow(PermissionKind.Microphone, Allowed(DeviceClass.AudioCapture), true));
        rows.Add(new PermissionRow(PermissionKind.Camera, Allowed(DeviceClass.VideoCapture), true));
        rows.Add(new PermissionRow(PermissionKind.Network, await Task.Run(FirewallAllows), true));
#else
        rows.Add(new PermissionRow(PermissionKind.Network, true, false));
        await Task.CompletedTask;
#endif
        rows.Add(new PermissionRow(PermissionKind.Bluetooth, true, false));
        return rows;
    }

#if WINDOWS
    // ---- Network access = Windows Firewall rule for this program.
    // The first time the app listens for a connection Windows asks "allow access?". If that was refused,
    // Windows keeps a block rule and never asks again, so the rule is replaced here (needs the admin question).

    static string ExePath => Environment.ProcessPath ?? "";

    static string RunPowerShell(string script, bool elevated, int timeoutMs)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var info = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        if (elevated)
        {
            info.Verb = "runas";
            info.UseShellExecute = true;
        }
        else
        {
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
        }
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("PowerShell did not start");
        var output = elevated ? "" : process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException("PowerShell took too long");
        }
        return output;
    }

    static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    // True when an enabled inbound "allow" rule exists for this program and no enabled inbound "block" rule.
    static bool FirewallAllows()
    {
        try
        {
            var script = "Get-NetFirewallApplicationFilter -Program " + Quote(ExePath) + " -ErrorAction SilentlyContinue | " +
                "Get-NetFirewallRule | ForEach-Object { [pscustomobject]@{E=[int]$_.Enabled;D=[int]$_.Direction;A=[int]$_.Action} } | ConvertTo-Json -Compress";
            var json = RunPowerShell(script, false, 15000).Trim();
            if (json.Length == 0) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<System.Text.Json.JsonElement> { doc.RootElement };
            var allow = false;
            var block = false;
            foreach (var item in items)
            {
                // Enabled 1 = true, Direction 1 = inbound, Action 2 = allow, 4 = block.
                if (item.GetProperty("E").GetInt32() != 1 || item.GetProperty("D").GetInt32() != 1) continue;
                var action = item.GetProperty("A").GetInt32();
                if (action == 2) allow = true;
                if (action == 4) block = true;
            }
            return allow && !block;
        }
        catch (Exception ex)
        {
            AppLog.Error("PERM", "reading the firewall rules failed", ex);
            return false;
        }
    }

    static void ApplyFirewallRule(bool allow)
    {
        try
        {
            var path = Quote(ExePath);
            var script = "Get-NetFirewallApplicationFilter -Program " + path + " -ErrorAction SilentlyContinue | Get-NetFirewallRule | Remove-NetFirewallRule; " +
                "New-NetFirewallRule -DisplayName 'BtChat' -Direction Inbound -Program " + path +
                " -Action " + (allow ? "Allow" : "Block") + " -Profile Any | Out-Null";
            RunPowerShell(script, true, 60000);
            AppLog.Write("PERM", $"firewall rule set to {(allow ? "allow" : "block")}");
        }
        catch (Exception ex)
        {
            // Most often: the user closed the administrator question.
            AppLog.Error("PERM", "changing the firewall rule failed", ex);
        }
    }

    static bool Allowed(DeviceClass deviceClass)
    {
        try
        {
            var status = DeviceAccessInformation.CreateFromDeviceClass(deviceClass).CurrentStatus;
            return status != DeviceAccessStatus.DeniedByUser && status != DeviceAccessStatus.DeniedBySystem;
        }
        catch (Exception ex)
        {
            AppLog.Error("PERM", "reading device access failed", ex);
            return true;
        }
    }
#endif

    public async Task SetAsync(PermissionKind kind, bool enable)
    {
#if WINDOWS
        if (kind == PermissionKind.Network)
        {
            await Task.Run(() => ApplyFirewallRule(enable));
            return;
        }
        var uri = kind switch
        {
            PermissionKind.Microphone => "ms-settings:privacy-microphone",
            PermissionKind.Camera => "ms-settings:privacy-webcam",
            _ => null
        };
        if (uri == null) return;
        try
        {
            await Launcher.Default.OpenAsync(new Uri(uri));
        }
        catch (Exception ex)
        {
            AppLog.Error("PERM", "opening Windows privacy settings failed", ex);
        }
#else
        await Task.CompletedTask;
#endif
    }
}
#endif
