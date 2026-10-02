using System.Text.Json;
using System.Text.Json.Serialization;

namespace BtChat;

public sealed class StoredMessage
{
    public string Text { get; set; } = "";
    public bool IsMine { get; set; }
    public bool IsFile { get; set; }
    public string? Location { get; set; }
    public DateTime Time { get; set; }
    public string? FailKey { get; set; }

    public static StoredMessage From(ChatMessage m) => new()
    {
        Text = m.Text,
        IsMine = m.IsMine,
        IsFile = m.IsFile,
        Location = m.Location,
        Time = m.Time,
        FailKey = m.FailKey
    };
}

[JsonSerializable(typeof(List<StoredMessage>))]
internal partial class HistoryJsonContext : JsonSerializerContext
{
}

public static class ChatHistory
{
    static readonly object gate = new();
    static long requested;
    static long written;

    static string FilePath => System.IO.Path.Combine(FileSystem.AppDataDirectory, "history.json");

    public static List<ChatMessage> Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new();
            var stored = JsonSerializer.Deserialize(File.ReadAllText(path), HistoryJsonContext.Default.ListStoredMessage);
            var list = stored?.Select(ChatMessage.Restore).ToList() ?? new();
            AppLog.Write("HISTORY", $"loaded {list.Count} messages");
            return list;
        }
        catch (Exception ex)
        {
            AppLog.Error("HISTORY", "load failed", ex);
            return new();
        }
    }

    // Call on the UI thread. Messages that are still transferring are not saved;
    // they are saved when they finish (see ChatMessage.Finished).
    public static void Save(IEnumerable<ChatMessage> messages)
    {
        var snapshot = messages.Where(m => !m.ShowProgress).Select(StoredMessage.From).ToList();
        var path = FilePath;
        var ticket = Interlocked.Increment(ref requested);
        Task.Run(() =>
        {
            lock (gate)
            {
                // A newer snapshot was already written: skip this stale one.
                if (ticket < written) return;
                written = ticket;
                try
                {
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, HistoryJsonContext.Default.ListStoredMessage));
                    File.Move(tmp, path, true);
                }
                catch (Exception ex)
                {
                    AppLog.Error("HISTORY", "save failed", ex);
                }
            }
        });
    }
}
