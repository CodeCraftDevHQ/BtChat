namespace BtChat;

public partial class ProxyPage : ContentPage
{
    readonly ProxyViewModel vm;

    public ProxyPage(ProxyViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        BindingContext = vm;
        vm.Host = this;
        vm.RefreshAddressesCommand.Execute(null);
    }

    async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();

    async Task CloseAsync()
    {
        try
        {
            vm.Host = null;
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("UI", "closing proxy page failed", ex);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }
}
