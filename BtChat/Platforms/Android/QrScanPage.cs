using BarcodeScanning;

namespace BtChat;

public sealed class QrScanPage : ContentPage
{
    readonly TaskCompletionSource<string?> result;
    readonly CameraView camera;
    readonly Label hint;
    bool done;

    public QrScanPage(TaskCompletionSource<string?> result)
    {
        this.result = result;
        var loc = Loc.Instance;
        FlowDirection = loc.Flow;
        camera = new CameraView
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            CameraEnabled = false,
            TapToFocusEnabled = true,
            VibrationOnDetected = false
        };
        camera.OnDetectionFinished += OnDetected;
        hint = new Label
        {
            Text = loc["qrScanHint"],
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(16, 12)
        };
        var cancel = new Button { Text = loc["cancel"], Margin = new Thickness(16, 0, 16, 16) };
        cancel.Clicked += (_, _) => Finish(null);
        var grid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            }
        };
        grid.Add(camera, 0, 0);
        grid.Add(hint, 0, 1);
        grid.Add(cancel, 0, 2);
        Content = grid;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!done) camera.CameraEnabled = true;
    }

    protected override void OnDisappearing()
    {
        camera.CameraEnabled = false;
        base.OnDisappearing();
        Finish(null);
    }

    void OnDetected(object? sender, OnDetectionFinishedEventArg e)
    {
        foreach (var barcode in e.BarcodeResults)
        {
            var text = string.IsNullOrEmpty(barcode.RawValue) ? barcode.DisplayValue : barcode.RawValue;
            if (QrPayload.TryParse(text, out _))
            {
                Finish(text);
                return;
            }
            hint.Text = Loc.Instance["qrInvalid"];
        }
    }

    void Finish(string? text)
    {
        if (done) return;
        done = true;
        camera.CameraEnabled = false;
        result.TrySetResult(text);
    }
}
