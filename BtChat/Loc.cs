using System.ComponentModel;
using System.Globalization;

namespace BtChat;

public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    static readonly Dictionary<string, (string Fa, string En)> table = new()
    {
        ["lang"] = ("English", "فارسی"),
        ["pick"] = ("انتخاب دستگاه جفت‌شده", "Select paired device"),
        ["refresh"] = ("بروزرسانی", "Refresh"),
        ["connect"] = ("اتصال", "Connect"),
        ["type"] = ("پیام خود را بنویسید...", "Type a message..."),
        ["send"] = ("ارسال", "Send"),
        ["idle"] = ("متصل نیست - منتظر اتصال", "Not connected - waiting"),
        ["connecting"] = ("در حال اتصال...", "Connecting..."),
        ["connected"] = ("متصل شد", "Connected"),
        ["failed"] = ("خطا در ارتباط", "Connection error"),
        ["ip"] = ("آدرس IP دستگاه مقابل", "Other device IP address"),
        ["connectIp"] = ("اتصال با IP", "Connect via IP"),
        ["myIp"] = ("آدرس این دستگاه:", "This device:"),
        ["disconnect"] = ("قطع", "Disconnect"),
        ["retrying"] = ("در حال تلاش برای اتصال مجدد...", "Reconnecting..."),
        ["log"] = ("لاگ", "Log"),
        ["copy"] = ("کپی همه", "Copy all"),
        ["clear"] = ("پاک کردن", "Clear"),
        ["btoff"] = ("بلوتوث خاموش است یا دسترسی داده نشده", "Bluetooth is off or permission denied")
    };

    public bool IsFa { get; private set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    public string this[string key] => table.TryGetValue(key, out var v) ? (IsFa ? v.Fa : v.En) : key;

    public FlowDirection Flow => IsFa ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Toggle()
    {
        IsFa = !IsFa;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
