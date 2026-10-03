using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BtChat;

public sealed class Conversation : ObservableObject
{
    string name;
    bool isCurrent;
    bool isLinked;
    int unread;

    public Conversation(string id, string name)
    {
        Id = id;
        this.name = name;
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Preview));
    }

    public string Id { get; }
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public string Name
    {
        get => name;
        set
        {
            if (SetProperty(ref name, value)) OnPropertyChanged(nameof(Initial));
        }
    }

    public string Initial
    {
        get
        {
            var text = name.Trim();
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
