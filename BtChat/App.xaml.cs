namespace BtChat;

public partial class App : Application
{
    readonly IServiceProvider services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        this.services = services;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(services.GetRequiredService<MainPage>()) { Title = "BtChat" };
        // App lock: remember when the app went away and ask for the PIN again after the chosen time.
        window.Deactivated += (_, _) => AppLock.OnBackground();
        window.Activated += (_, _) => AppLock.OnForeground();
        return window;
    }
}
