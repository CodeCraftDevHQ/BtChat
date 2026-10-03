namespace BtChat;

public sealed class MediaViewerPage : ContentPage
{
    readonly ChatMessage message;
    readonly IReceivedFileStore files;
    readonly MediaPlayerView? player;
    readonly Image? image;
    double startScale = 1;
    double startX;
    double startY;

    public MediaViewerPage(ChatMessage message, IReceivedFileStore files)
    {
        this.message = message;
        this.files = files;
        var loc = Loc.Instance;
        FlowDirection = loc.Flow;
        BackgroundColor = Colors.Black;

        var title = new Label
        {
            Text = message.Text,
            TextColor = Colors.White,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center
        };
        var openWith = HeaderButton("↗");
        openWith.Clicked += async (_, _) => await OpenExternalAsync();
        var close = HeaderButton("✕");
        close.Clicked += async (_, _) => await CloseAsync();

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            Padding = new Thickness(12, 8),
            ColumnSpacing = 6
        };
        header.Add(title, 0, 0);

        var body = new Grid();
        if (message.IsImage)
        {
            image = new Image
            {
                Source = message.Thumb,
                Aspect = Aspect.AspectFit,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            var pinch = new PinchGestureRecognizer();
            pinch.PinchUpdated += OnPinch;
            var pan = new PanGestureRecognizer();
            pan.PanUpdated += OnPan;
            var reset = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
            reset.Tapped += (_, _) => ResetZoom();
            image.GestureRecognizers.Add(pinch);
            image.GestureRecognizers.Add(pan);
            image.GestureRecognizers.Add(reset);
            body.Add(image);

            var zoomIn = HeaderButton("＋");
            zoomIn.Clicked += (_, _) => Zoom(1.4);
            var zoomOut = HeaderButton("－");
            zoomOut.Clicked += (_, _) => Zoom(1 / 1.4);
            header.Add(zoomOut, 1, 0);
            header.Add(zoomIn, 2, 0);
        }
        else
        {
            player = new MediaPlayerView
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            body.Add(player);
            if (message.IsAudio)
            {
                body.Add(new Label
                {
                    Text = "🎵",
                    FontSize = 96,
                    TextColor = Colors.White,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    InputTransparent = true
                });
            }
        }
        header.Add(openWith, 3, 0);
        header.Add(close, 4, 0);

        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            }
        };
        root.Add(header, 0, 0);
        root.Add(body, 0, 1);
        Content = root;
    }

    static Button HeaderButton(string text) => new()
    {
        Text = text,
        TextColor = Colors.White,
        BackgroundColor = Colors.Transparent,
        FontSize = 20,
        Padding = new Thickness(10, 0),
        WidthRequest = 44,
        HeightRequest = 40,
        MinimumWidthRequest = 0,
        MinimumHeightRequest = 0
    };

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (player != null && message.Location != null) player.Source = message.Location;
    }

    protected override void OnDisappearing()
    {
        if (player != null)
        {
            player.Source = null;
            player.Handler?.DisconnectHandler();
        }
        base.OnDisappearing();
    }

    async Task CloseAsync()
    {
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", "close viewer failed", ex);
        }
    }

    async Task OpenExternalAsync()
    {
        if (message.Location == null) return;
        try
        {
            await files.OpenAsync(message.Location, message.Text);
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", "open with other app failed", ex);
        }
    }

    void Zoom(double factor)
    {
        if (image == null) return;
        var scale = Math.Clamp(image.Scale * factor, 1, 6);
        image.Scale = scale;
        if (scale <= 1) ResetZoom();
    }

    void ResetZoom()
    {
        if (image == null) return;
        image.Scale = 1;
        image.TranslationX = 0;
        image.TranslationY = 0;
    }

    void OnPinch(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (image == null) return;
        switch (e.Status)
        {
            case GestureStatus.Started:
                startScale = image.Scale;
                break;
            case GestureStatus.Running:
                image.Scale = Math.Clamp(startScale * e.Scale, 1, 6);
                break;
            case GestureStatus.Completed:
                if (image.Scale <= 1) ResetZoom();
                break;
        }
    }

    void OnPan(object? sender, PanUpdatedEventArgs e)
    {
        if (image == null || image.Scale <= 1) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                startX = image.TranslationX;
                startY = image.TranslationY;
                break;
            case GestureStatus.Running:
                image.TranslationX = startX + e.TotalX;
                image.TranslationY = startY + e.TotalY;
                break;
        }
    }
}
