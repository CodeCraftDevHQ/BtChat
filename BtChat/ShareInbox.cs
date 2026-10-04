namespace BtChat;

// A file another app shared with BtChat. Location is the original (content:// uri or path): nothing is copied.
public sealed record SharedFile(string Name, string Location, long Size);

// Files shared from other apps wait here until the main screen can show the "send to" list
// (the share can arrive before the screen exists, e.g. when BtChat is started by the share).
public static class ShareInbox
{
    static readonly object gate = new();
    static readonly List<SharedFile> pending = new();

    public static event Action? Arrived;

    public static void Push(IEnumerable<SharedFile> items)
    {
        lock (gate) pending.AddRange(items);
        MainThread.BeginInvokeOnMainThread(() => Arrived?.Invoke());
    }

    // Puts files back without announcing them again (the screen was not ready).
    public static void Return(IEnumerable<SharedFile> items)
    {
        lock (gate) pending.InsertRange(0, items);
    }

    public static List<SharedFile> Take()
    {
        lock (gate)
        {
            var copy = pending.ToList();
            pending.Clear();
            return copy;
        }
    }
}
