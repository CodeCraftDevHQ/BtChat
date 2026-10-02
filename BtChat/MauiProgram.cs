namespace BtChat;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if ANDROID
        builder.Services.AddSingleton<IBluetoothTransport, AndroidBluetoothTransport>();
#elif WINDOWS
        builder.Services.AddSingleton<IBluetoothTransport, WindowsBluetoothTransport>();
#endif
        builder.Services.AddSingleton<TcpTransport>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
