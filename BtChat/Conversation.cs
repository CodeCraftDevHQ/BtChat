using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BtChat;

public sealed class Conversation : ObservableObject
{
    string peerName;
    string? alias;
    bool isCurrent;
    bool isLinked;
    int unread;

    public Conversation(string id, string name)
    {
        Id = id;
        peerName = name;
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Preview));
    }

    public string Id { get; }
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    // Index (in pin order) of the pinned message the top bar currently shows; not saved.
    public int PinCursor { get; set; }

    public string PeerName
    {
        get => peerName;
        set
        {
            if (SetProperty(ref peerName, value)) NotifyName();
        }
    }

    public string? Alias
    {
        get => alias;
        set
        {
            if (SetProperty(ref alias, value)) NotifyName();
        }
    }

    public string Name => string.IsNullOrWhiteSpace(alias) ? peerName : alias;

    void NotifyName()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Initial));
    }

    public string Initial
    {
        get
        {
            var text = Name.Trim();
            return text.Length == 0 ? "?" : StringInfo.GetNextTextElement(text).ToUpperInvariant();
        }
    }

    public string Preview
    {
        get
        {
            var last = Messages.LastOrDefault();
            return last == null ? "" : last.Display;
        }
    }

    public bool IsCurrent
    {
        get => isCurrent;
        set
        {
            if (SetProperty(ref isCurrent, value)) OnPropertyChanged(nameof(RowColor));
        }
    }

    public bool IsLinked
    {
        get => isLinked;
        set => SetProperty(ref isLinked, value);
    }

    public int Unread
    {
        get => unread;
        set
        {
            if (!SetProperty(ref unread, value)) return;
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(UnreadText));
        }
    }

    public bool HasUnread => unread > 0;
    public string UnreadText => unread > 99 ? "99+" : unread.ToString();
    public Color RowColor => isCurrent ? Color.FromArgb("#333B82F6") : Colors.Transparent;
}
