namespace BtChat;

// Three short pages that explain how to use the app. Shown on the first run (and from Settings later).
// Skipping or closing it marks it as seen, so it does not appear again by itself.
public sealed class GuidePage : ContentPage
{
    const string SeenKey = "guideSeen";
    static bool shownThisRun;

    static readonly (string Icon, string Title, string Text)[] Pages =
    {
        ("🔗", "guide1Title", "guide1Text"),
        ("💬", "guide2Title", "guide2Text"),
        ("🔒", "guide3Title", "guide3Text")
    };

    readonly Label icon;
    readonly Label title;
    readonly Label text;
    readonly Label dots;
    readonly Button back;
    readonly Button next;
    int index;

    public static async Task ShowFirstRunAsync()
    {
        if (shownThisRun || Preferences.Default.Get(SeenKey, false)) return;
        shownThisRun = true;
        try
        {
            // Give the window a moment to be ready for a page on top of it.
            await Task.Delay(600);
            var host = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (host != null) await ShowAsync(host);
        }
        catch (Exception ex)
        {
            AppLog.Error("GUIDE", "showing the first-run guide failed", ex);
        }
    }

    public static Task ShowAsync(Page host) => host.Navigation.PushModalAsync(new GuidePage(), true);

    public GuidePage()
    {
        FlowDirection = Loc.Instance.Flow;
        this.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#121212"));
        var loc = Loc.Instance;

        icon = new Label { FontSize = 72, HorizontalOptions = LayoutOptions.Center };
        title = new Label { FontSize = 24, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center };
        text = new Label { FontSize = 16, HorizontalTextAlignment = TextAlignment.Center, TextColor = Colors.Gray };
        dots = new Label { FontSize = 18, HorizontalOptions = LayoutOptions.Center };

        back = new Button { Text = loc["guideBack"], BackgroundColor = Colors.Transparent, TextColor = Colors.Gray };
        back.Clicked += (_, _) => Go(index - 1);
        next = new Button { Text = loc["guideNext"] };
        next.Clicked += async (_, _) =>
        {
            if (index < Pages.Length - 1) Go(index + 1);
            else await CloseAsync();
        };
        var skip = new Button { Text = loc["guideSkip"], BackgroundColor = Colors.Transparent, TextColor = Colors.Gray, FontSize = 13 };
        skip.Clicked += async (_, _) => await CloseAsync();

        var buttons = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            ColumnSpacing = 12
        };
        buttons.Add(back, 0);
        buttons.Add(next, 1);

        var content = new VerticalStackLayout
        {
            Spacing = 18,
            VerticalOptions = LayoutOptions.Center,
            Padding = new Thickness(28, 0)
        };
        content.Add(icon);
        content.Add(title);
        content.Add(text);

        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            },
            Padding = new Thickness(16, 24, 16, 16),
            RowSpacing = 12
        };
        root.Add(new ScrollView { Content = content }, 0, 0);
        root.Add(dots, 0, 1);
        root.Add(buttons, 0, 2);
        root.Add(skip, 0, 3);
        Content = root;
        Go(0);
    }

    void Go(int page)
    {
        index = Math.Clamp(page, 0, Pages.Length - 1);
        var loc = Loc.Instance;
        var (pageIcon, pageTitle, pageText) = Pages[index];
        icon.Text = pageIcon;
        title.Text = loc[pageTitle];
        text.Text = loc[pageText];
        dots.Text = string.Join(" ", Enumerable.Range(0, Pages.Length).Select(i => i == index ? "●" : "○"));
        back.IsVisible = index > 0;
        next.Text = loc[index == Pages.Length - 1 ? "guideDone" : "guideNext"];
    }

    async Task CloseAsync()
    {
        Preferences.Default.Set(SeenKey, true);
        try
        {
            if (Navigation.ModalStack.LastOrDefault() == this) await Navigation.PopModalAsync(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("GUIDE", "closing the guide failed", ex);
        }
    }

    // Closing it with the back button also counts as seen.
    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }
}
