#if ANDROID
using BarcodeScanning;
#endif
namespace BtChat;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        CrashReport.Install();
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if ANDROID || WINDOWS
        builder.ConfigureMauiHandlers(handlers => {
            handlers.AddHandler<MediaPlayerView, MediaPlayerViewHandler>();
            handlers.AddHandler<VideoFrameView, VideoFrameViewHandler>();
        });
#endif
#if ANDROID
        builder.Services.AddSingleton<IPermissionGate, AndroidPermissionGate>();
        builder.Services.AddSingleton<IPermissionCenter, AndroidPermissionCenter>();
        builder.Services.AddSingleton<IBluetoothTransport, AndroidBluetoothTransport>();
        builder.Services.AddSingleton<IReceivedFileStore, AndroidReceivedFileStore>();
#elif WINDOWS
        builder.Services.AddSingleton<IPermissionGate, DefaultPermissionGate>();
        builder.Services.AddSingleton<IPermissionCenter, DefaultPermissionCenter>();
        builder.Services.AddSingleton<IBluetoothTransport, WindowsBluetoothTransport>();
        builder.Services.AddSingleton<IReceivedFileStore, WindowsReceivedFileStore>();
#endif
#if ANDROID
        builder.UseBarcodeScanning();
        builder.Services.AddSingleton<IQrScanner, AndroidQrScanner>();
        builder.Services.AddSingleton<IVoiceRecorder, AndroidVoiceRecorder>();
        builder.Services.AddSingleton<IAudioPlayer, AndroidAudioPlayer>();
        builder.Services.AddSingleton<ICallAudio, AndroidCallAudio>();
        builder.Services.AddSingleton<ICallAlert, AndroidCallAlert>();
        builder.Services.AddSingleton<IMessageAlert, AndroidMessageAlert>();
        builder.Services.AddSingleton<IBiometricAuth, AndroidBiometricAuth>();
        builder.Services.AddSingleton<ICallVideo, AndroidCallVideo>();
        builder.Services.AddSingleton<IKeepAlive, AndroidKeepAlive>();
        builder.Services.AddSingleton<IFileSource, AndroidFileSource>();
#else
        builder.Services.AddSingleton<IQrScanner, NoQrScanner>();
        builder.Services.AddSingleton<IVoiceRecorder, NoVoiceRecorder>();
#if WINDOWS
        builder.Services.AddSingleton<ICallAudio, WindowsCallAudio>();
        builder.Services.AddSingleton<ICallAlert, WindowsCallAlert>();
        builder.Services.AddSingleton<ICallVideo, WindowsCallVideo>();
#else
        builder.Services.AddSingleton<ICallAudio, NoCallAudio>();
        builder.Services.AddSingleton<ICallAlert, NoCallAlert>();
        builder.Services.AddSingleton<ICallVideo, NoCallVideo>();
#endif
        builder.Services.AddSingleton<IMessageAlert, NoMessageAlert>();
        builder.Services.AddSingleton<IBiometricAuth, NoBiometricAuth>();
#if WINDOWS
        builder.Services.AddSingleton<IAudioPlayer, WindowsAudioPlayer>();
#else
        builder.Services.AddSingleton<IAudioPlayer, NoAudioPlayer>();
#endif
        builder.Services.AddSingleton<IKeepAlive, NoKeepAlive>();
        builder.Services.AddSingleton<IFileSource, DefaultFileSource>();
#endif
        builder.Services.AddSingleton<TcpTransport>();
        builder.Services.AddSingleton<DiscoveryService>();
#if ANDROID
        builder.Services.AddSingleton<ISystemProxy, AndroidSystemProxy>();
        builder.Services.AddSingleton<IVpnTunnel, AndroidVpnTunnel>();
#elif WINDOWS
        builder.Services.AddSingleton<ISystemProxy, WindowsSystemProxy>();
        builder.Services.AddSingleton<IVpnTunnel, NoVpnTunnel>();
#else
        builder.Services.AddSingleton<ISystemProxy, NoSystemProxy>();
        builder.Services.AddSingleton<IVpnTunnel, NoVpnTunnel>();
#endif
        builder.Services.AddSingleton<ProxyClientViewModel>();
        builder.Services.AddSingleton<ProxyViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
