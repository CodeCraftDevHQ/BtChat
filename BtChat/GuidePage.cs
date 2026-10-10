namespace BtChat;

// Short pages that explain how to use the app. Shown on the first run (and from Settings later).
// On the very first start the first slide is the language choice. Skipping or closing the guide marks it
// as seen, so it does not appear again by itself.
public sealed class GuidePage : ContentPage
{
    const string SeenKey = "guideSeen";
    static bool shownThisRun;
    static readonly TaskCompletionSource opened = new();
    static readonly TaskCompletionSource closed = new();

    static readonly (string Icon, string Title, string Text)[] InfoPages =
    {
        ("🔗", "guide1Title", "guide1Text"),
        ("💬", "guide2Title", "guide2Text"),
        ("🔒", "guide3Title", "guide3Text")
    };

    readonly bool withLanguage;
    readonly int pageCount;
    readonly Label icon;
    readonly Label title;
    readonly Label text;
    readonly Label dots;
    readonly Button back;
    readonly Button next;
    readonly Button skip;
    readonly HorizontalStackLayout languageButtons;
    int index;

    // The first start waits here until the guide (with the language choice) is closed, so permission
    // questions do not pile up on top of it. If the guide does not show up in a few seconds, it goes on.
    public static async Task WaitForFirstRunAsync()
    {
        if (Preferences.Default.Get(SeenKey, false)) return;
        var first = await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (first == opened.Task) await closed.Task;
    }

    public static async Task ShowFirstRunAsync()
    {
        if (shownThisRun || Preferences.Default.Get(SeenKey, false)) return;
        shownThisRun = true;
        try
        {
            // Give the window a moment to be ready for a page on top of it.
            await Task.Delay(600);
            var host = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (host != null)
            {
                await ShowAsync(host);
                opened.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("GUIDE", "showing the first-run guide failed", ex);
        }
    }

    public static Task ShowAsync(Page host) => host.Navigation.PushModalAsync(new GuidePage(), true);

    public GuidePage()
    {
        withLanguage = !Loc.HasSavedLanguage;
        pageCount = InfoPages.Length + (withLanguage ? 1 : 0);
        this.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#121212"));

        icon = new Label { FontSize = 72, HorizontalOptions = LayoutOptions.Center };
        title = new Label { FontSize = 24, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center };
        text = new Label { FontSize = 16, HorizontalTextAlignment = TextAlignment.Center, TextColor = Colors.Gray };
        dots = new Label { FontSize = 18, HorizontalOptions = LayoutOptions.Center };

        var persian = new Button { Text = "فارسی", WidthRequest = 130 };
        persian.Clicked += (_, _) => ChooseLanguage(true);
        var english = new Button { Text = "English", WidthRequest = 130 };
        english.Clicked += (_, _) => ChooseLanguage(false);
        languageButtons = new HorizontalStackLayout { Spacing = 14, HorizontalOptions = LayoutOptions.Center, IsVisible = false };
        languageButtons.Add(persian);
        languageButtons.Add(english);

        back = new Button { BackgroundColor = Colors.Transparent, TextColor = Colors.Gray };
        back.Clicked += (_, _) => Go(index - 1);
        next = new Button();
        next.Clicked += async (_, _) =>
        {
            if (index < pageCount - 1) Go(index + 1);
            else await CloseAsync();
        };
        skip = new Button { BackgroundColor = Colors.Transparent, TextColor = Colors.Gray, FontSize = 13 };
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
        content.Add(languageButtons);

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

    bool OnLanguagePage => withLanguage && index == 0;

    // The language slide shows both languages, because the user has not chosen one yet.
    void ChooseLanguage(bool persian)
    {
        var vm = IPlatformApplication.Current?.Services.GetService<MainViewModel>();
        if (vm != null) vm.LanguageIndex = persian ? 0 : 1; // switches the whole app and keeps its screens in sync
        else if (Loc.Instance.IsFa != persian) Loc.Instance.Toggle();
        Loc.Instance.SaveChoice();
        AppLog.Write("GUIDE", $"language chosen: {(persian ? "fa" : "en")}");
        Go(1);
    }

    void Go(int page)
    {
        index = Math.Clamp(page, 0, pageCount - 1);
        var loc = Loc.Instance;
        FlowDirection = loc.Flow;
        languageButtons.IsVisible = OnLanguagePage;
        if (OnLanguagePage)
        {
            icon.Text = "🌐";
            title.Text = "زبان / Language";
            text.Text = "زبان برنامه را انتخاب کن\nChoose the app language";
            next.Text = "Next / بعدی";
            skip.Text = "Skip / رد کردن";
        }
        else
        {
            var (pageIcon, pageTitle, pageText) = InfoPages[index - (withLanguage ? 1 : 0)];
            icon.Text = pageIcon;
            title.Text = loc[pageTitle];
            text.Text = loc[pageText];
            next.Text = loc[index == pageCount - 1 ? "guideDone" : "guideNext"];
            skip.Text = loc["guideSkip"];
        }
        back.Text = loc["guideBack"];
        dots.Text = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => i == index ? "●" : "○"));
        back.IsVisible = index > 0;
    }

    async Task CloseAsync()
    {
        Preferences.Default.Set(SeenKey, true);
        // Leaving without choosing keeps the current language, so the app does not ask again.
        if (!Loc.HasSavedLanguage) Loc.Instance.SaveChoice();
        try
        {
            if (Navigation.ModalStack.LastOrDefault() == this) await Navigation.PopModalAsync(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("GUIDE", "closing the guide failed", ex);
        }
        closed.TrySetResult();
    }

    // Closing it with the back button also counts as seen.
    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }
}
