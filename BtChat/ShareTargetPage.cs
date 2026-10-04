namespace BtChat;

// "Send to…" list shown when files are shared into BtChat: the connected device first, then earlier chats.
public sealed class ShareTargetPage : ContentPage
{
    readonly TaskCompletionSource<Conversation?> result = new();

    public ShareTargetPage(IReadOnlyList<SharedFile> items, IReadOnlyList<Conversation> chats)
    {
        var loc = Loc.Instance;
        FlowDirection = loc.Flow;
        this.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#121212"));

        var title = new Label { Text = loc["shareTo"], FontSize = 22, FontAttributes = FontAttributes.Bold };
        title.SetAppThemeColor(Label.TextColorProperty, Colors.Black, Colors.White);
        var summary = new Label
        {
            Text = items.Count == 1 ? items[0].Name : string.Format(loc["shareFiles"], items.Count),
            FontSize = 14,
            LineBreakMode = LineBreakMode.MiddleTruncation,
            TextColor = Colors.Gray
        };

        var list = new VerticalStackLayout { Spacing = 8 };
        foreach (var chat in chats) list.Add(Row(chat));
        if (chats.Count == 0)
            list.Add(new Label { Text = loc["shareNoChats"], TextColor = Colors.Gray, Margin = new Thickness(0, 24), HorizontalTextAlignment = TextAlignment.Center });

        var cancel = new Button { Text = loc["cancel"], HorizontalOptions = LayoutOptions.Fill };
        cancel.Clicked += async (_, _) => await CloseAsync(null);

        var root = new Grid
        {
            Padding = new Thickness(16, 20, 16, 16),
            RowSpacing = 12,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            }
        };
        root.Add(title, 0, 0);
        root.Add(summary, 0, 1);
        root.Add(new ScrollView { Content = list }, 0, 2);
        root.Add(cancel, 0, 3);
        Content = root;
    }

    public Task<Conversation?> Result => result.Task;

    View Row(Conversation chat)
    {
        var loc = Loc.Instance;
        var initial = new Label
        {
            Text = chat.Initial,
            TextColor = Colors.White,
            FontAttributes = FontAttributes.Bold,
            FontSize = 18,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        var avatar = new Border
        {
            WidthRequest = 44,
            HeightRequest = 44,
            Stroke = Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 22 },
            BackgroundColor = chat.IsLinked ? Color.FromArgb("#22A06B") : Color.FromArgb("#3B82F6"),
            Content = initial
        };
        var name = new Label { Text = chat.Name, FontSize = 17, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetAppThemeColor(Label.TextColorProperty, Colors.Black, Colors.White);
        var info = new Label
        {
            Text = chat.IsLinked ? loc["shareConnectedTag"] : chat.Preview,
            FontSize = 13,
            LineBreakMode = LineBreakMode.TailTruncation,
            TextColor = chat.IsLinked ? Color.FromArgb("#22A06B") : Colors.Gray
        };
        var texts = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { name, info } };
        var grid = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }
        };
        grid.Add(avatar, 0, 0);
        grid.Add(texts, 1, 0);
        var row = new Border
        {
            Padding = new Thickness(12, 10),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Stroke = chat.IsLinked ? Color.FromArgb("#22A06B") : Colors.Gray,
            StrokeThickness = chat.IsLinked ? 2 : 1,
            BackgroundColor = Colors.Transparent,
            Content = grid
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await CloseAsync(chat);
        row.GestureRecognizers.Add(tap);
        return row;
    }

    async Task CloseAsync(Conversation? chat)
    {
        result.TrySetResult(chat);
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("SHARE", "closing chooser failed", ex);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        result.TrySetResult(null);
    }
}
