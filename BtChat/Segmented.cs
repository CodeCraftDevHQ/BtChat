using System.Collections;

namespace BtChat;

// A small "pill" switch with several options (like iOS segmented control): the chosen one is highlighted.
public sealed class Segmented : Border
{
    public static readonly BindableProperty ItemsProperty =
        BindableProperty.Create(nameof(Items), typeof(IEnumerable), typeof(Segmented), null,
            propertyChanged: (b, _, _) => ((Segmented)b).Rebuild());

    public static readonly BindableProperty SelectedIndexProperty =
        BindableProperty.Create(nameof(SelectedIndex), typeof(int), typeof(Segmented), 0, BindingMode.TwoWay,
            propertyChanged: (b, _, _) => ((Segmented)b).Refresh());

    readonly Grid grid = new() { ColumnSpacing = 2 };
    readonly List<(Border Pill, Label Text)> cells = new();

    public Segmented()
    {
        Padding = new Thickness(3);
        StrokeThickness = 0;
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 };
        this.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#E5E7EB"), Color.FromArgb("#3A3A3F"));
        Content = grid;
    }

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    void Rebuild()
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        cells.Clear();
        var index = 0;
        foreach (var item in Items ?? Array.Empty<string>())
        {
            var at = index++;
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var label = new Label
            {
                Text = item?.ToString() ?? "",
                FontSize = 14,
                FontAttributes = FontAttributes.Bold,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            var pill = new Border
            {
                Padding = new Thickness(8, 0),
                MinimumHeightRequest = 38,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                Content = label
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => SelectedIndex = at;
            pill.GestureRecognizers.Add(tap);
            grid.Add(pill, at, 0);
            cells.Add((pill, label));
        }
        Refresh();
    }

    void Refresh()
    {
        for (var i = 0; i < cells.Count; i++)
        {
            var on = i == SelectedIndex;
            cells[i].Pill.BackgroundColor = on ? Color.FromArgb("#3B82F6") : Colors.Transparent;
            cells[i].Text.TextColor = on ? Colors.White : Colors.Gray;
        }
    }
}
