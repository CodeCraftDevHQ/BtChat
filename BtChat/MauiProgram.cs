#if ANDROID
using BarcodeScanning;
#endif
namespace BtChat;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if ANDROID
        builder.Services.AddSingleton<IBluetoothTransport, AndroidBluetoothTransport>();
        builder.Services.AddSingleton<IReceivedFileStore, AndroidReceivedFileStore>();
#elif WINDOWS
        builder.Services.AddSingleton<IBluetoothTransport, WindowsBluetoothTransport>();
        builder.Services.AddSingleton<IReceivedFileStore, WindowsReceivedFileStore>();
#endif
#if ANDROID
        builder.UseBarcodeScanning();
        builder.Services.AddSingleton<IQrScanner, AndroidQrScanner>();
#else
        builder.Services.AddSingleton<IQrScanner, NoQrScanner>();
#endif
        builder.Services.AddSingleton<TcpTransport>();
        builder.Services.AddSingleton<DiscoveryService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
