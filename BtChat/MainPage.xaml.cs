namespace BtChat;

public partial class MainPage : ContentPage
{
    readonly MainViewModel vm;

    public MainPage(MainViewModel vm)
    {
        InitializeComponent();
        this.vm = vm;
        BindingContext = vm;
        Drawer.TranslationX = vm.IsDrawerOpen ? 0 : ClosedOffset;
        vm.ScrollRequested += () => Dispatcher.Dispatch(ScrollToEnd);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsDrawerOpen)) _ = AnimateDrawerAsync();
        };
        SizeChanged += (_, _) => FitDrawer();
        Loaded += async (_, _) =>
        {
            ScrollToEnd();
            await vm.InitAsync();
        };
    }

    double ClosedOffset => -(Drawer.WidthRequest + 24);

    void FitDrawer()
    {
        if (Width <= 0) return;
        var width = Math.Clamp(Width * 0.86, 260, 340);
        if (Math.Abs(Drawer.WidthRequest - width) < 0.5) return;
        Drawer.WidthRequest = width;
        if (!vm.IsDrawerOpen) Drawer.TranslationX = ClosedOffset;
    }

    async Task AnimateDrawerAsync()
    {
        var target = vm.IsDrawerOpen ? 0 : ClosedOffset;
        await Drawer.TranslateToAsync(target, 0, 220, Easing.CubicOut);
    }

    void ScrollToEnd()
    {
        var count = vm.Messages.Count;
        if (count == 0) return;
        MessagesView.ScrollTo(count - 1, position: ScrollToPosition.End, animate: false);
    }

    protected override bool OnBackButtonPressed()
    {
        if (!vm.IsDrawerOpen) return base.OnBackButtonPressed();
        vm.IsDrawerOpen = false;
        return true;
    }
}
