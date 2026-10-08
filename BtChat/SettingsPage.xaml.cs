namespace BtChat;

public partial class SettingsPage : ContentPage
{
    readonly MainViewModel vm;

    public SettingsPage(MainViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        BindingContext = vm;
        VersionLabel.Text = $"BtChat · {Loc.Instance["version"]} {AppInfo.Current.VersionString}";
    }

    async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();

    const string WindowsDownloadUrl = "https://github.com/CodeCraftDevHQ/BtChat";

    async void OnWindowsDownloadClicked(object? sender, EventArgs e)
    {
        try
        {
            await Launcher.Default.OpenAsync(new Uri(WindowsDownloadUrl));
        }
        catch (Exception ex)
        {
            AppLog.Error("UI", "opening Windows download link failed", ex);
            await DisplayAlert(Loc.Instance["setAbout"], Loc.Instance["openLinkFailed"], Loc.Instance["close"]);
        }
    }

    async void OnLogClicked(object? sender, EventArgs e)
    {
        await CloseAsync();
        vm.ToggleLogCommand.Execute(null);
    }

    async Task CloseAsync()
    {
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("UI", "closing settings failed", ex);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }
}
