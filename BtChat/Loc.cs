using System.ComponentModel;
using System.Globalization;

namespace BtChat;

public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    static readonly Dictionary<string, (string Fa, string En)> table = new()
    {
        ["lang"] = ("English", "فارسی"),
        ["mode"] = ("نوع اتصال", "Connection type"),
        ["copyText"] = ("کپی متن", "Copy text"),
        ["openFile"] = ("باز کردن فایل", "Open file"),
        ["openFolder"] = ("رفتن به پوشه", "Go to folder"),
        ["savedIn"] = ("فایل در این پوشه ذخیره شده:", "Saved in:"),
        ["noApp"] = ("برنامه‌ای برای باز کردن این فایل پیدا نشد", "No app found to open this file"),
        ["share"] = ("اشتراک‌گذاری", "Share"),
        ["cancel"] = ("انصراف", "Cancel"),
        ["clearHistory"] = ("پاک کردن تاریخچه چت", "Clear chat history"),
        ["clearHistoryAsk"] = ("همه پیام‌ها از لیست چت پاک شوند؟ فایل‌های ذخیره‌شده روی دستگاه پاک نمی‌شوند.", "Remove all messages from the chat? Files saved on this device are not deleted."),
        ["deleteMessage"] = ("حذف پیام", "Delete message"),
        ["deleteFile"] = ("حذف فایل", "Delete file"),
        ["deleteFileAsk"] = ("این فایل از حافظه دستگاه هم پاک می‌شود. ادامه می‌دهید؟", "This file will also be deleted from this device. Continue?"),
        ["deleteFileFailed"] = ("حذف فایل از حافظه انجام نشد (شاید قبلاً پاک شده باشد). پیام از چت حذف شد.", "Could not delete the file from storage (it may already be gone). The message was removed from the chat."),
        ["delete"] = ("حذف", "Delete"),
        ["cancelSend"] = ("لغو ارسال", "Cancel sending"),
        ["fileCanceled"] = ("ارسال فایل لغو شد", "File sending canceled"),
        ["fileCanceledByPeer"] = ("ارسال فایل توسط فرستنده لغو شد", "Sender canceled the file"),
        ["noNetwork"] = ("شبکه‌ای پیدا نشد؛ به وای‌فای وصل شوید یا هات‌اسپات را روشن کنید", "No network found; join Wi-Fi or turn on hotspot"),
        ["badIp"] = ("آدرس IP معتبر نیست (مثال: 192.168.1.5)", "Invalid IP address (e.g. 192.168.1.5)"),
        ["selfIp"] = ("این آدرس مربوط به همین دستگاه است؛ IP دستگاه مقابل را وارد کنید", "That is this device's own address; enter the other device's IP"),
        ["tcpTimeout"] = ("پاسخی از دستگاه مقابل نیامد (IP یا شبکه را بررسی کنید)", "No response from the other device (check IP / network)"),
        ["tcpRefused"] = ("دستگاه پیدا شد ولی برنامه آنجا آماده نیست (برنامه را باز کنید)", "Device reached but the app isn't listening there (open the app)"),
        ["search"] = ("جستجوی دستگاه‌ها", "Find devices"),
        ["searching"] = ("در حال جستجو...", "Searching..."),
        ["noneFound"] = ("دستگاهی پیدا نشد؛ دستگاه مقابل هم باید برنامه را باز و حالت وای‌فای را انتخاب کرده باشد", "No device found; the other device must have the app open in Wi-Fi mode"),
        ["foundHeader"] = ("دستگاه‌های پیدا شده (برای اتصال بزنید):", "Devices found (tap to connect):"),
        ["showQr"] = ("نمایش QR", "Show QR"),
        ["scanQr"] = ("اسکن QR", "Scan QR"),
        ["close"] = ("بستن", "Close"),
        ["qrTitle"] = ("این QR را با دستگاه مقابل اسکن کنید", "Scan this QR with the other device"),
        ["qrScanHint"] = ("QR نمایش‌داده‌شده روی دستگاه مقابل را داخل کادر بگیرید", "Point the camera at the QR shown on the other device"),
        ["qrInvalid"] = ("این QR مربوط به BtChat نیست", "This QR code is not from BtChat"),
        ["qrNoCamera"] = ("دسترسی به دوربین داده نشد", "Camera permission denied"),
        ["qrFailed"] = ("اسکن QR انجام نشد", "QR scan failed"),
        ["fileFailed"] = ("انتقال فایل ناموفق بود", "File transfer failed"),
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
