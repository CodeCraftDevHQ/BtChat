namespace BtChat;

public partial class App : Application
{
    readonly IServiceProvider services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        this.services = services;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new Window(services.GetRequiredService<MainPage>()) { Title = "BtChat" };
}
