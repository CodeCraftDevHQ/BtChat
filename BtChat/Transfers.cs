using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace BtChat;

// A received file that did not arrive completely. Location is the partial file on disk (null: nothing was written).
public sealed class FailedReceive
{
    public required Guid Key { get; init; }
    public required ChatMessage Message { get; init; }
    public long Expected { get; init; }
    public string? Location { get; init; }
}

// Remembers failed received files by their transfer key, so that when the sender offers the same file again
// the same chat bubble is reused and the transfer continues from the bytes that are already on disk.
public sealed class ResumeRegistry
{
    readonly ConcurrentDictionary<Guid, FailedReceive> map = new();

    public void Set(FailedReceive item) => map[item.Key] = item;

    public bool TryGet(Guid key, [NotNullWhen(true)] out FailedReceive? item) => map.TryGetValue(key, out item);

    public void Remove(Guid key) => map.TryRemove(key, out _);

    public void RemoveMessage(ChatMessage message)
    {
        foreach (var pair in map)
            if (ReferenceEquals(pair.Value.Message, message))
                map.TryRemove(pair.Key, out _);
    }
}
