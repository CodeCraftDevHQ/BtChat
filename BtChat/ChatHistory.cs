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
    public string SenderName { get; set; } = "";
    public long SizeBytes { get; set; }
    public double DurationSeconds { get; set; }
    public long PartialBytes { get; set; }
    public string? TransferKey { get; set; }

    public static StoredMessage From(ChatMessage m) => new()
    {
        Text = m.Text,
        IsMine = m.IsMine,
        IsFile = m.IsFile,
        Location = m.Location,
        Time = m.Time,
        // A file that is still being transferred when this is saved comes back as "interrupted" after a restart.
        FailKey = m.FailKey ?? (m.IsFile && m.ShowProgress ? "fileInterrupted" : null),
        SenderName = m.SenderName,
        SizeBytes = m.SizeBytes,
        DurationSeconds = m.DurationSeconds,
        PartialBytes = m.ShowProgress ? m.LastDone : m.PartialBytes,
        TransferKey = m.TransferKey == Guid.Empty ? null : m.TransferKey.ToString("N")
    };
}

public sealed class StoredChat
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Alias { get; set; }
    public List<StoredMessage> Messages { get; set; } = new();
}

[JsonSerializable(typeof(List<StoredChat>))]
[JsonSerializable(typeof(List<StoredMessage>))]
internal partial class HistoryJsonContext : JsonSerializerContext
{
}

public static class ChatHistory
{
    public const string LegacyId = "legacy";

    static readonly object gate = new();
    static long requested;
    static long written;

    static string ChatsPath => System.IO.Path.Combine(FileSystem.AppDataDirectory, "chats.json");
    static string LegacyPath => System.IO.Path.Combine(FileSystem.AppDataDirectory, "history.json");

    public static List<Conversation> Load()
    {
        try
        {
            if (File.Exists(ChatsPath))
            {
                var stored = JsonSerializer.Deserialize(File.ReadAllText(ChatsPath), HistoryJsonContext.Default.ListStoredChat) ?? new();
                var chats = stored.Select(Restore).ToList();
                AppLog.Write("HISTORY", $"loaded {chats.Count} chats");
                return chats;
            }
            if (File.Exists(LegacyPath))
            {
                var old = JsonSerializer.Deserialize(File.ReadAllText(LegacyPath), HistoryJsonContext.Default.ListStoredMessage) ?? new();
                AppLog.Write("HISTORY", $"migrating {old.Count} old messages");
                if (old.Count == 0) return new();
                var chat = Restore(new StoredChat { Id = LegacyId, Name = Loc.Instance["oldChat"], Messages = old });
                return new() { chat };
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("HISTORY", "load failed", ex);
        }
        return new();
    }

    static Conversation Restore(StoredChat stored)
    {
        var chat = new Conversation(stored.Id, stored.Name) { Alias = stored.Alias };
        foreach (var m in stored.Messages) chat.Messages.Add(ChatMessage.Restore(m));
        return chat;
    }

    public static void Save(IEnumerable<Conversation> chats)
    {
        var snapshot = chats.Select(c => new StoredChat
        {
            Id = c.Id,
            Name = c.PeerName,
            Alias = c.Alias,
            Messages = c.Messages.Where(m => !m.ShowProgress || m.IsFile).Select(StoredMessage.From).ToList()
        }).ToList();
        var path = ChatsPath;
        var ticket = Interlocked.Increment(ref requested);
        Task.Run(() =>
        {
            lock (gate)
            {
                if (ticket < written) return;
                written = ticket;
                try
                {
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, HistoryJsonContext.Default.ListStoredChat));
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
