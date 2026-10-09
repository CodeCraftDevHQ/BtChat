namespace BtChat;

// Notification for a new message or file when the app is not on screen.
public interface IMessageAlert
{
    // chatId groups the notifications of one chat (a newer message replaces/extends the older one).
    void Show(string chatId, string title, string text);

    // Removes the shown message notifications (the user came back to the app, or turned them off).
    void ClearAll();
}

#if !ANDROID
public sealed class NoMessageAlert : IMessageAlert
{
    public void Show(string chatId, string title, string text)
    {
    }

    public void ClearAll()
    {
    }
}
#endif
