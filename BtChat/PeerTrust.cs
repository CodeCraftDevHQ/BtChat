namespace BtChat;

// Remembers the identity key (as a fingerprint) of every device that connected with encryption, so a later
// change of that key can be noticed. A plain text file with one "device-id fingerprint" pair per line.
public static class PeerTrust
{
    static readonly object gate = new();
    static Dictionary<string, string>? known;

    static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "known-peers.txt");

    static Dictionary<string, string> Load()
    {
        if (known != null) return known;
        var loaded = new Dictionary<string, string>();
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var parts = line.Split(' ', 2);
                    if (parts.Length == 2 && parts[0].Length > 0) loaded[parts[0]] = parts[1];
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("SEC", "reading known peers failed", ex);
        }
        known = loaded;
        return known;
    }

    public static string? Get(string peerId)
    {
        lock (gate) return Load().TryGetValue(peerId, out var fingerprint) ? fingerprint : null;
    }

    public static void Set(string peerId, string fingerprint)
    {
        lock (gate)
        {
            var all = Load();
            all[peerId] = fingerprint;
            try
            {
                File.WriteAllLines(FilePath, all.Select(p => p.Key + " " + p.Value));
            }
            catch (Exception ex)
            {
                AppLog.Error("SEC", "saving known peers failed", ex);
            }
        }
    }

    // Fingerprints are shown in groups of four ("A1B2 C3D4 ..."); compare them without spaces and case.
    public static bool Same(string? a, string? b) =>
        a != null && b != null && string.Equals(Compact(a), Compact(b), StringComparison.OrdinalIgnoreCase);

    public static string Compact(string fingerprint) => fingerprint.Replace(" ", "");
}
