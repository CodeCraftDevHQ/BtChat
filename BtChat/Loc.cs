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
        ["edit"] = ("ویرایش", "Edit"),
        ["edited"] = ("ویرایش‌شده", "edited"),
        ["editTitle"] = ("ویرایش پیام", "Edit message"),
        ["editNeedsConnection"] = ("برای ویرایش پیام باید به همین دستگاه وصل باشید تا تغییر برای او هم اعمال شود", "Connect to this device to edit the message, so the change reaches it too"),
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
        ["queued"] = ("در صف ارسال...", "Waiting in queue..."),
        ["fileWaiting"] = ("در انتظار شروع دریافت...", "Waiting to be received..."),
        ["transferCount"] = ("{0} فایل در حال انتقال", "{0} file(s) transferring"),
        ["concurrentFiles"] = ("ارسال همزمان چند فایل", "Send several files at once"),
        ["notifChannel"] = ("اتصال BtChat", "BtChat connection"),
        ["notifConnected"] = ("متصل است؛ اتصال در پس‌زمینه نگه داشته می‌شود", "Connected; keeping the link alive in the background"),
        ["notifRetrying"] = ("در حال اتصال مجدد...", "Reconnecting..."),
        ["battery"] = ("🔋 باتری", "🔋 Battery"),
        ["batteryAlready"] = ("اجرای بدون محدودیت باتری از قبل فعال است. اگر باز هم برنامه در پس‌زمینه بسته می‌شود، تنظیمات «شروع خودکار» یا «اجرا در پس‌زمینه» گوشی را هم بررسی کنید.", "Battery is already unrestricted. If the app is still closed in the background, also check your phone's autostart / background-run settings."),
        ["fileMissing"] = ("فایل اصلی دیگر در حافظه نیست (جابه‌جا یا پاک شده است)", "The original file is no longer on this device (moved or deleted)"),
        ["fileFailed"] = ("انتقال فایل ناموفق بود", "File transfer failed"),
        ["fileInterrupted"] = ("انتقال فایل قطع شد", "File transfer interrupted"),
        ["retry"] = ("🔄 تلاش مجدد", "🔄 Retry"),
        ["retryNeedsConnection"] = ("برای تلاش مجدد ابتدا به همان دستگاه وصل شوید", "Connect to the same device first to retry"),
        ["retryRequested"] = ("درخواست ارسال مجدد به فرستنده داده شد...", "Asked the sender to send it again..."),
        ["retryDenied"] = ("فرستنده دیگر این فایل را ندارد", "The sender no longer has this file"),
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
        ["chats"] = ("چت‌ها", "Chats"),
        ["connSettings"] = ("تنظیمات اتصال", "Connection settings"),
        ["myName"] = ("نام این دستگاه", "This device's name"),
        ["myNameHint"] = ("این نام در اولین اتصال به دستگاه مقابل معرفی می‌شود و کنار پیام‌ها و پوشه فایل‌ها می‌آید", "Introduced to the other device on connect; shown beside messages and used for the files folder"),
        ["noChats"] = ("هنوز چتی نیست؛ به یک دستگاه وصل شوید تا چت آن اینجا ساخته شود", "No chats yet; connect to a device and its chat appears here"),
        ["noChatSelected"] = ("از منوی ☰ به یک دستگاه وصل شوید یا یک چت را انتخاب کنید", "Use the ☰ menu to connect to a device or pick a chat"),
        ["unknownDevice"] = ("دستگاه ناشناس", "Unknown device"),
        ["oldChat"] = ("پیام‌های قبلی", "Earlier messages"),
        ["notLinkedHere"] = ("برای ارسال پیام، به این دستگاه وصل شوید", "Connect to this device to send messages"),
        ["deleteChat"] = ("حذف چت", "Delete chat"),
        ["deleteChatAsk"] = ("این چت و همه پیام‌هایش از برنامه حذف می‌شود. فایل‌های ذخیره‌شده روی دستگاه پاک نمی‌شوند.", "This chat and all its messages will be removed from the app. Files saved on this device are not deleted."),
        ["chatInUse"] = ("چت دستگاه متصل را نمی‌توان حذف کرد؛ اول اتصال را قطع کنید", "Cannot delete the chat of the connected device; disconnect first"),
        ["permBluetoothTitle"] = ("دسترسی به بلوتوث", "Bluetooth access"),
        ["permBluetoothWhy"] = ("برای دیدن فهرست دستگاه‌های جفت‌شده و ارسال پیام و فایل به آن‌ها، برنامه به مجوز «دستگاه‌های نزدیک» (بلوتوث) نیاز دارد. برنامه دستگاه جدیدی جستجو نمی‌کند و به موقعیت مکانی شما دسترسی ندارد.", "To list your paired devices and exchange messages and files with them, the app needs the Nearby devices (Bluetooth) permission. It does not scan for new devices and does not use your location."),
        ["permCameraTitle"] = ("دسترسی به دوربین", "Camera access"),
        ["permCameraWhy"] = ("دوربین فقط برای اسکن QR دستگاه مقابل و اتصال سریع لازم است. تصویر دوربین ذخیره یا ارسال نمی‌شود.", "The camera is only used to scan the other device's QR code for a quick connection. Nothing is saved or sent."),
        ["permNotificationsTitle"] = ("نمایش اعلان", "Notifications"),
        ["permNotificationsWhy"] = ("برای اینکه اتصال در پس‌زمینه قطع نشود، برنامه یک اعلان ثابت نشان می‌دهد. این مجوز فقط برای نمایش همین اعلان است.", "To keep the connection alive in the background, the app shows one ongoing notification. This permission is only used for that notification."),
        ["permStorageTitle"] = ("دسترسی به حافظه", "Storage access"),
        ["permStorageWhy"] = ("برای ذخیره فایل‌های دریافتی در پوشه Downloads دستگاه، به دسترسی حافظه نیاز است.", "Storage access is needed to save received files into the Downloads folder."),
        ["permMicrophoneTitle"] = ("دسترسی به میکروفون", "Microphone access"),
        ["permMicrophoneWhy"] = ("میکروفون فقط وقتی که دکمه ضبط صدا را نگه می‌دارید و برای ضبط پیام صوتی استفاده می‌شود. صدای ضبط‌شده فقط برای دستگاه متصل فرستاده می‌شود و جای دیگری ارسال نمی‌شود.", "The microphone is only used while you hold the voice button, to record a voice message. The recording is sent only to the connected device, nowhere else."),
        ["permCameraCaptureTitle"] = ("دسترسی به دوربین", "Camera access"),
        ["permCameraCaptureWhy"] = ("دوربین فقط وقتی که دکمه دوربین را نگه می‌دارید و برای فیلم‌برداری استفاده می‌شود. فیلم فقط برای دستگاه متصل فرستاده می‌شود و برنامه پنهانی از دوربین استفاده نمی‌کند.", "The camera is only used when you hold the camera button, to record a video. It is sent only to the connected device, and the app never uses the camera in the background."),
        ["recordSlideCancel"] = ("برای لغو، دکمه را به کنار بکشید", "Slide to cancel"),
        ["recordReleaseCancel"] = ("رها کنید تا لغو شود", "Release to cancel"),
        ["audioSpeed"] = ("سرعت پخش", "Playback speed"),
        ["playFailed"] = ("پخش فایل صوتی انجام نشد", "Could not play the audio"),
        ["recordFailed"] = ("ضبط صدا شروع نشد", "Could not start recording"),
        ["captureFailed"] = ("دوربین باز نشد یا فیلم ذخیره نشد", "Could not open the camera or save the video"),
        ["permContinue"] = ("ادامه", "Continue"),
        ["permNotNow"] = ("الان نه", "Not now"),
        ["permBlocked"] = ("این مجوز قبلاً رد شده و سیستم اجازه درخواست دوباره را نمی‌دهد. برای فعال کردن آن، تنظیمات برنامه را باز کنید و مجوز را روشن کنید.", "This permission was denied and the system will not ask again. Open the app settings and turn it on."),
        ["permOpenSettings"] = ("باز کردن تنظیمات", "Open settings"),
        ["btNoPermission"] = ("برای استفاده از بلوتوث مجوز لازم است؛ دکمه «بروزرسانی» را بزنید", "Bluetooth permission is needed; tap Refresh"),
        ["btTurnOnTitle"] = ("بلوتوث خاموش است", "Bluetooth is off"),
        ["btTurnOnAsk"] = ("برای اتصال باید بلوتوث روشن باشد. روشن شود؟", "Bluetooth must be on to connect. Turn it on?"),
        ["btTurnOn"] = ("روشن کردن", "Turn on"),
        ["viewImage"] = ("نمایش تصویر", "View image"),
        ["shareTo"] = ("ارسال به…", "Send to…"),
        ["shareFiles"] = ("{0} فایل", "{0} files"),
        ["shareConnectedTag"] = ("● متصل", "● Connected"),
        ["shareNoChats"] = ("هنوز گفتگویی وجود ندارد. ابتدا به یک دستگاه وصل شوید.", "No chats yet. Connect to a device first."),
        ["shareCopying"] = ("در حال آماده‌سازی فایل...", "Preparing the file..."),
        ["fileNotSent"] = ("هنوز ارسال نشده؛ پس از اتصال دکمه‌ی تلاش مجدد را بزنید", "Not sent yet - press Retry once connected"),
        ["connection"] = ("اتصال", "Connection"),
        ["connectedTo"] = ("متصل به {0}", "Connected to {0}"),
        ["easyConnect"] = ("اتصال آسان", "Easy connect"),
        ["tileSearch"] = ("جستجوی دستگاه‌ها در شبکه", "Find devices on the network"),
        ["tileSearchHint"] = ("دستگاه مقابل هم برنامه را باز کرده باشد", "The other device must have the app open"),
        ["manualConnect"] = ("اتصال دستی", "Manual connection"),
        ["manualHint"] = ("آدرس IP دستگاه مقابل را بنویسید. همان را در «اتصال دستی» دستگاه مقابل زیر «آدرس این دستگاه» می‌بینید.", "Type the other device's IP address. You can read it under This device in its Manual connection section."),
        ["settings"] = ("تنظیمات", "Settings"),
        ["setProfile"] = ("پروفایل", "Profile"),
        ["setAppearance"] = ("ظاهر", "Appearance"),
        ["setLanguage"] = ("زبان", "Language"),
        ["setTheme"] = ("پوسته", "Theme"),
        ["themeAuto"] = ("خودکار", "Auto"),
        ["themeLight"] = ("روشن", "Light"),
        ["themeDark"] = ("تیره", "Dark"),
        ["setTransfer"] = ("انتقال فایل", "File transfer"),
        ["concurrentFilesHint"] = ("در Wi-Fi چند فایل هم‌زمان فرستاده می‌شود. روی بلوتوث همیشه یکی‌یکی.", "On Wi-Fi several files are sent at once. Bluetooth always sends one by one."),
        ["setAdvanced"] = ("پیشرفته", "Advanced"),
        ["setAbout"] = ("درباره", "About"),
        ["version"] = ("نسخه", "Version"),
        ["speed"] = ("سرعت پخش", "Playback speed"),
        ["subtitles"] = ("زیرنویس", "Subtitles"),
        ["subtitleChoose"] = ("انتخاب فایل زیرنویس (SRT / VTT)", "Choose subtitle file (SRT / VTT)"),
        ["subtitleOff"] = ("خاموش کردن زیرنویس", "Turn subtitles off"),
        ["subtitleEarlier"] = ("زیرنویس زودتر نمایش داده شود (−۰٫۵ ثانیه)", "Show subtitles earlier (−0.5 s)"),
        ["subtitleLater"] = ("زیرنویس دیرتر نمایش داده شود (+۰٫۵ ثانیه)", "Show subtitles later (+0.5 s)"),
        ["subtitleBigger"] = ("بزرگ‌تر کردن متن زیرنویس", "Bigger subtitle text"),
        ["subtitleSmaller"] = ("کوچک‌تر کردن متن زیرنویس", "Smaller subtitle text"),
        ["subtitleFailed"] = ("خواندن زیرنویس ممکن نشد (فقط SRT و VTT)", "Could not read the subtitles (SRT and VTT only)"),
        ["playFailed"] = ("این فایل در پخش‌کننده‌ی داخلی باز نشد. با دکمه‌ی ↗ با برنامه‌ی دیگری باز کنید.", "This file could not be played here. Use the ↗ button to open it in another app."),
        ["playMedia"] = ("پخش در برنامه", "Play in app"),
        ["openWith"] = ("باز کردن با برنامه‌های دیگر", "Open with another app"),
        ["renameChat"] = ("تغییر نام چت", "Rename chat"),
        ["renameChatAsk"] = ("نام جدید چت (خالی = نام خود دستگاه). پوشه فایل‌های بعدی هم با همین نام ساخته می‌شود.", "New chat name (empty = the device's own name). New files go to a folder with this name."),
        ["save"] = ("ذخیره", "Save"),
        ["logTitle"] = ("گزارش (لاگ)", "Log"),
        ["secShort"] = ("ثانیه", "s"),
        ["minShort"] = ("دقیقه", "min"),
        ["ok"] = ("باشه", "OK"),
        ["helpBluetoothTitle"] = ("اتصال با بلوتوث", "Connect with Bluetooth"),
        ["helpBluetooth"] = (
            "۱. بلوتوث هر دو دستگاه را روشن کنید.\n\n۲. دو دستگاه را با هم «جفت» (Pair) کنید. در گوشی: تنظیمات ‹ بلوتوث. در ویندوز: Settings ‹ Bluetooth & devices ‹ Add device. کد نمایش‌داده‌شده را روی هر دو تأیید کنید. این کار فقط یک بار لازم است.\n\n۳. برنامه را روی هر دو دستگاه باز کنید.\n\n۴. در این کشو «نوع اتصال» را روی بلوتوث بگذارید، نام دستگاه مقابل را از «انتخاب دستگاه جفت‌شده» بردارید (اگر در لیست نبود «بروزرسانی» را بزنید) و دکمه «اتصال» را بزنید. کافی است اتصال را فقط روی یکی از دو دستگاه بزنید.\n\n۵. بعد از اتصال، دو دستگاه خودشان را به هم معرفی می‌کنند و چتی با نام دستگاه مقابل ساخته می‌شود.\n\nاگر ارتباط قطع شود، برنامه خودش دوباره تلاش می‌کند.",
            "1. Turn Bluetooth on for both devices.\n\n2. Pair the two devices. On a phone: Settings > Bluetooth. On Windows: Settings > Bluetooth & devices > Add device. Confirm the code on both. This is needed only once.\n\n3. Open the app on both devices.\n\n4. In this drawer set the connection type to Bluetooth, pick the other device under Select paired device (tap Refresh if it is missing) and press Connect. Pressing Connect on just one of the two devices is enough.\n\n5. After connecting, the devices introduce themselves and a chat with the other device's name is created.\n\nIf the link drops, the app retries by itself."),
        ["helpWifiTitle"] = ("اتصال با وای‌فای / هات‌اسپات", "Connect with Wi-Fi / hotspot"),
        ["helpWifi"] = (
            "۱. هر دو دستگاه باید در یک شبکه باشند: یا هر دو به یک وای‌فای وصل شوند، یا یکی از آن‌ها هات‌اسپات (Hotspot) روشن کند و دیگری به آن وصل شود. اینترنت لازم نیست.\n\n۲. برنامه را روی هر دو دستگاه باز کنید و «نوع اتصال» را روی Wi-Fi / IP بگذارید.\n\n۳. یکی از این راه‌ها را انتخاب کنید:\n• «جستجوی دستگاه‌ها» و لمس نام دستگاه مقابل (ساده‌ترین راه)\n• «نمایش QR» روی یک دستگاه و «اسکن QR» روی گوشی دیگر\n• نوشتن IP دستگاه مقابل در بخش «اتصال دستی»\n\n۴. اگر ویندوز پیام فایروال نشان داد، اجازه دسترسی در شبکه خصوصی (Private) را بدهید.\n\n۵. بعد از اتصال دو دستگاه یکدیگر را معرفی می‌کنند. بلوتوث و وای‌فای یک دستگاه در همان یک چت قرار می‌گیرند.",
            "1. Both devices must be on the same network: either join the same Wi-Fi, or let one device turn on its hotspot and the other join it. Internet is not needed.\n\n2. Open the app on both devices and set the connection type to Wi-Fi / IP.\n\n3. Pick one way:\n• Find devices, then tap the other device's name (easiest)\n• Show QR on one device and Scan QR on the other phone\n• Type the other device's IP address in Manual connection\n\n4. If Windows shows a firewall prompt, allow access on Private networks.\n\n5. After connecting, the devices introduce themselves. Bluetooth and Wi-Fi of the same device share one chat."),
        ["helpIpTitle"] = ("اتصال با آدرس IP", "Connect by IP address"),
        ["helpIp"] = (
            "۱. روی دستگاه مقابل در کشوی کناری، بخش «اتصال دستی» را باز کنید و در خط «آدرس این دستگاه» آدرس را ببینید (مثل 192.168.1.5). اگر خالی بود «بروزرسانی» را بزنید.\n\n۲. همان آدرس را اینجا بنویسید و «اتصال با IP» را بزنید.\n\n۳. هر دو دستگاه باید در یک شبکه باشند. اگر دستگاه مقابل چند آدرس نشان داد، آدرسی را امتحان کنید که شروعش شبیه آدرس دستگاه شماست.\n\n۴. آدرس خود دستگاه را وارد نکنید.",
            "1. On the other device open the Manual connection section in the side drawer and read the address in the This device line (like 192.168.1.5). Tap Refresh if it is empty.\n\n2. Type that address here and press Connect via IP.\n\n3. Both devices must be on the same network. If the other device shows several addresses, try the one that starts like your own device's address.\n\n4. Do not enter this device's own address."),
        ["helpQuickTitle"] = ("جستجو و QR", "Search and QR"),
        ["helpQuick"] = (
            "جستجوی دستگاه‌ها:\nدستگاه مقابل هم باید برنامه را باز کرده و حالت Wi-Fi / IP را انتخاب کرده باشد. بعد از جستجو نامش در لیست می‌آید و با لمس آن وصل می‌شوید.\n\nQR:\nروی یکی از دو دستگاه «نمایش QR» را بزنید و روی گوشی اندروید دیگر «اسکن QR» را بزنید و دوربین را روی کد بگیرید. اتصال خودکار برقرار می‌شود و نیازی به نوشتن IP نیست.\n\nنکته: ویندوز فقط می‌تواند QR را نمایش دهد؛ اسکن با گوشی اندروید انجام می‌شود. اولین بار اجازه دوربین پرسیده می‌شود.",
            "Find devices:\nThe other device must have the app open in Wi-Fi / IP mode. After the search its name appears in the list; tap it to connect.\n\nQR:\nPress Show QR on one device and Scan QR on the other Android phone, then point the camera at the code. It connects automatically, no IP typing needed.\n\nNote: Windows can only show the QR; scanning is done with an Android phone. The camera permission is asked the first time."),
        ["btoff"] = ("بلوتوث خاموش است یا دسترسی داده نشده", "Bluetooth is off or permission denied")
    };

    public bool IsFa { get; private set; } = SavedLanguageIsFa();

    // The language chosen in the first-run dialog or in Settings is remembered; until then it is English.
    public static bool HasSavedLanguage
    {
        get
        {
            try
            {
                return Preferences.Default.Get("lang", "") is "fa" or "en";
            }
            catch
            {
                return false;
            }
        }
    }

    public void SaveChoice()
    {
        try { Preferences.Default.Set("lang", IsFa ? "fa" : "en"); } catch { }
    }

    static bool SavedLanguageIsFa()
    {
        try
        {
            var saved = Preferences.Default.Get("lang", "");
            if (saved == "fa") return true;
            if (saved == "en") return false;
        }
        catch
        {
        }
        return false;
    }

    public string this[string key] => table.TryGetValue(key, out var v) ? (IsFa ? v.Fa : v.En) : key;

    public FlowDirection Flow => IsFa ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Toggle()
    {
        IsFa = !IsFa;
        SaveChoice();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
