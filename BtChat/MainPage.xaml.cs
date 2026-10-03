namespace BtChat;

public partial class MainPage : ContentPage
{
    const double WideThreshold = 900;
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
        LogCard.WidthRequest = Math.Max(240, Math.Min(520, Width - 32));
        LogCard.HeightRequest = Math.Clamp(Height * 0.78, 240, 560);
        var wide = Width >= WideThreshold;
        if (vm.IsWide != wide) vm.IsWide = wide;
        var width = wide ? 340 : Math.Clamp(Width * 0.86, 260, 340);
        Drawer.WidthRequest = width;
        if (wide)
        {
            Drawer.TranslationX = 0;
            MainArea.Margin = new Thickness(width, 0, 0, 0);
            return;
        }
        MainArea.Margin = new Thickness(0);
        Drawer.TranslationX = vm.IsDrawerOpen ? 0 : ClosedOffset;
    }

    async Task AnimateDrawerAsync()
    {
        if (vm.IsWide) return;
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
        if (vm.ShowLog)
        {
            vm.ShowLog = false;
            return true;
        }
        if (!vm.IsDrawerOpen || vm.IsWide) return base.OnBackButtonPressed();
        vm.IsDrawerOpen = false;
        return true;
    }
}
