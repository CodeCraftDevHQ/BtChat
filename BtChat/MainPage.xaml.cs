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
        vm.RemoteVideoFrame += frame => RemoteVideo.Show(frame);
        vm.LocalVideoFrame += frame => LocalVideo.Show(frame);
        vm.RemoteVideoCleared += () => RemoteVideo.Clear();
        vm.LocalVideoCleared += () => LocalVideo.Clear();
        vm.LocalMirrorChanged += mirror => LocalVideo.SetMirror(mirror);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CallMenuOpen)) MainThread.BeginInvokeOnMainThread(AnimateCallMenu);
        };
        MediaButton.HoldDown += vm.MediaButtonDown;
        MediaButton.HoldMoved += vm.MediaButtonMoved;
        MediaButton.HoldUp += vm.MediaButtonUp;
        Drawer.TranslationX = vm.IsDrawerOpen ? 0 : ClosedOffset;
        vm.ScrollRequested += () => Dispatcher.Dispatch(ScrollToEnd);
        vm.ScrollToMessageRequested += message => Dispatcher.Dispatch(() => ScrollToMessage(message));
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsDrawerOpen)) _ = AnimateDrawerAsync();
        };
        SizeChanged += (_, _) => { FitDrawer(); FitCall(); };
        MessagesView.SizeChanged += (_, _) => FitBubbles();
        Loaded += async (_, _) =>
        {
            ScrollToEnd();
            await vm.InitAsync();
        };
    }

    void OnAudioDragStarted(object? sender, EventArgs e)
    {
        if (sender is Slider { BindingContext: ChatMessage message }) message.AudioSeeking = true;
    }

    void OnAudioDragCompleted(object? sender, EventArgs e)
    {
        if (sender is not Slider { BindingContext: ChatMessage message } slider) return;
        vm.SeekAudio(message, slider.Value);
        message.AudioSeeking = false;
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

    void FitBubbles()
    {
        var width = MessagesView.Width;
        if (width <= 0) return;
        var max = Math.Max(120, width * 0.8);
        if (Math.Abs(vm.BubbleMaxWidth - max) > 0.5) vm.BubbleMaxWidth = max;
    }

    async Task AnimateDrawerAsync()
    {
        if (vm.IsWide) return;
        var target = vm.IsDrawerOpen ? 0 : ClosedOffset;
        await Drawer.TranslateToAsync(target, 0, 220, Easing.CubicOut);
    }

    void ScrollToMessage(ChatMessage message)
    {
        if (!vm.Messages.Contains(message)) return;
        MessagesView.ScrollTo(message, position: ScrollToPosition.Center, animate: true);
    }

    void ScrollToEnd()
    {
        var count = vm.Messages.Count;
        if (count == 0) return;
        MessagesView.ScrollTo(count - 1, position: ScrollToPosition.End, animate: false);
    }

    protected override bool OnBackButtonPressed()
    {
        if (vm.ShowCall) return true;
        if (vm.ShowLog)
        {
            vm.ShowLog = false;
            return true;
        }
        if (!vm.IsDrawerOpen || vm.IsWide) return base.OnBackButtonPressed();
        vm.IsDrawerOpen = false;
        return true;
    }

    void FitCall()
    {
        var width = Math.Clamp(Width * 0.26, 84, 170);
        LocalPreview.WidthRequest = width;
        LocalPreview.HeightRequest = width * 4 / 3;
    }

    async void AnimateCallMenu()
    {
        try
        {
            CallMenu.CancelAnimations();
            if (vm.CallMenuOpen)
            {
                CallMenu.Opacity = 0;
                CallMenu.TranslationY = 40;
                CallMenu.IsVisible = true;
                await Task.WhenAll(CallMenu.FadeTo(1, 180), CallMenu.TranslateTo(0, 0, 180, Easing.CubicOut));
            }
            else if (CallMenu.IsVisible)
            {
                await Task.WhenAll(CallMenu.FadeTo(0, 140), CallMenu.TranslateTo(0, 40, 140, Easing.CubicIn));
                if (!vm.CallMenuOpen) CallMenu.IsVisible = false;
            }
        }
        catch
        {
            CallMenu.IsVisible = vm.CallMenuOpen;
        }
    }
}
