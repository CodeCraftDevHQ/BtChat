namespace BtChat;

public static class LocalDevice
{
    const int MaxName = 40;
    static readonly string id = LoadId();

    public static string Id => id;

    public static string Name
    {
        get
        {
            var saved = Clean(Preferences.Default.Get("deviceName", ""));
            return saved.Length > 0 ? saved : AutoName();
        }
        set
        {
            var clean = Clean(value);
            if (clean.Length == 0) Preferences.Default.Remove("deviceName");
            else Preferences.Default.Set("deviceName", clean);
        }
    }

    static string LoadId()
    {
        var saved = Preferences.Default.Get("deviceId", "");
        if (saved.Length > 0) return saved;
        var created = Guid.NewGuid().ToString("N");
        Preferences.Default.Set("deviceId", created);
        return created;
    }

    static string AutoName()
    {
        string name;
        try { name = Clean(DeviceInfo.Current.Name); }
        catch { name = ""; }
        if (name.Length == 0
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("android", StringComparison.OrdinalIgnoreCase)
            || name.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            name = "Device-" + id[..4].ToUpperInvariant();
        return name;
    }

    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var text = new string(raw.Where(c => c != '|' && !char.IsControl(c)).ToArray()).Trim();
        return text.Length > MaxName ? text[..MaxName].TrimEnd() : text;
    }

    public static string SafeFolder(string? name)
    {
        const string forbidden = "\\/:*?\"<>|";
        var text = new string((name ?? "").Where(c => !char.IsControl(c) && forbidden.IndexOf(c) < 0).ToArray()).Trim().TrimEnd('.');
        if (text.Length > MaxName) text = text[..MaxName].TrimEnd().TrimEnd('.');
        if (text.Length == 0) return "Unknown";
        string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
        if (reserved.Contains(text, StringComparer.OrdinalIgnoreCase)) text += "_";
        return text;
    }
}
