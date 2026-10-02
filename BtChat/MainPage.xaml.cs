namespace BtChat;

public partial class MainPage : ContentPage
{
    public MainPage(MainViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        vm.Messages.CollectionChanged += (_, _) =>
        {
            if (vm.Messages.Count > 0)
                MessagesView.ScrollTo(vm.Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        };
        Loaded += async (_, _) => await vm.InitAsync();
    }
}
