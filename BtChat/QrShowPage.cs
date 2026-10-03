using Microsoft.Maui.Controls.Shapes;

namespace BtChat;

public sealed class QrShowPage : ContentPage
{
    public QrShowPage(string payload, string addressesText)
    {
        var loc = Loc.Instance;
        FlowDirection = loc.Flow;
        var png = QrImage.Create(payload);
        var image = new Image
        {
            Source = ImageSource.FromStream(() => new MemoryStream(png)),
            WidthRequest = 280,
            HeightRequest = 280,
            Aspect = Aspect.AspectFit
        };
        var frame = new Border
        {
            BackgroundColor = Colors.White,
            Stroke = Colors.Transparent,
            Padding = new Thickness(12),
            HorizontalOptions = LayoutOptions.Center,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(16) },
            Content = image
        };
        var close = new Button { Text = loc["close"] };
        close.Clicked += async (_, _) =>
        {
            try { await Navigation.PopModalAsync(); } catch { }
        };
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24),
                Spacing = 16,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label
                    {
                        Text = loc["qrTitle"],
                        FontSize = 18,
                        FontAttributes = FontAttributes.Bold,
                        HorizontalTextAlignment = TextAlignment.Center
                    },
                    frame,
                    new Label { Text = addressesText, TextColor = Colors.Gray, HorizontalTextAlignment = TextAlignment.Center },
                    close
                }
            }
        };
    }
}
