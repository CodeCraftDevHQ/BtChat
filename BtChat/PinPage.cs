namespace BtChat;

// A full-screen page that asks for a PIN. Used for the lock screen and for the questions in Settings.
public sealed class PinPage : ContentPage
{
    readonly TaskCompletionSource<string?> result = new();
    readonly Func<string, Task<string?>> validate;
    readonly Func<Task<bool>>? useFingerprint;
    readonly bool allowCancel;
    readonly Entry entry;
    readonly Label problem;
    readonly Button confirm;
    bool done;
    bool fingerprintTried;

    // validate returns a message when the PIN is not accepted and null when it is.
    public PinPage(string title, string message, bool allowCancel, Func<string, Task<string?>> validate, Func<Task<bool>>? useFingerprint = null)
    {
        this.allowCancel = allowCancel;
        this.validate = validate;
        this.useFingerprint = useFingerprint;
        FlowDirection = Loc.Instance.Flow;
        this.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#121212"));

        entry = new Entry
        {
            IsPassword = true,
            Keyboard = Keyboard.Numeric,
            MaxLength = 8,
            Placeholder = "••••",
            FontSize = 26,
            HorizontalTextAlignment = TextAlignment.Center,
            WidthRequest = 220
        };
        entry.Completed += async (_, _) => await SubmitAsync();

        problem = new Label { TextColor = Colors.Red, IsVisible = false, HorizontalTextAlignment = TextAlignment.Center };
        confirm = new Button { Text = Loc.Instance["lockOk"], WidthRequest = 220 };
        confirm.Clicked += async (_, _) => await SubmitAsync();

        var stack = new VerticalStackLayout
        {
            Spacing = 14,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Padding = new Thickness(24)
        };
        stack.Add(new Label { Text = "🔒", FontSize = 44, HorizontalOptions = LayoutOptions.Center });
        stack.Add(new Label { Text = title, FontSize = 22, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center });
        stack.Add(new Label { Text = message, TextColor = Colors.Gray, HorizontalTextAlignment = TextAlignment.Center });
        stack.Add(entry);
        stack.Add(problem);
        stack.Add(confirm);
        if (useFingerprint != null)
        {
            var fingerprint = new Button { Text = Loc.Instance["lockUseFingerprint"], WidthRequest = 220 };
            fingerprint.Clicked += async (_, _) => await TryFingerprintAsync();
            stack.Add(fingerprint);
        }
        if (allowCancel)
        {
            var cancel = new Button { Text = Loc.Instance["cancel"], WidthRequest = 220, BackgroundColor = Colors.Transparent, TextColor = Colors.Gray };
            cancel.Clicked += async (_, _) => await FinishAsync(null);
            stack.Add(cancel);
        }
        Content = new ScrollView { Content = stack };

        Appearing += async (_, _) =>
        {
            // The lock screen asks for the fingerprint by itself the first time it shows.
            if (this.useFingerprint != null && !fingerprintTried)
            {
                fingerprintTried = true;
                await Task.Delay(300);
                await TryFingerprintAsync();
            }
            else
            {
                entry.Focus();
            }
        };
    }

    // Shows the page on top of everything and returns the PIN that was accepted (null: cancelled).
    public async Task<string?> ShowAsync(Page host)
    {
        await host.Navigation.PushModalAsync(this, false);
        return await result.Task;
    }

    async Task SubmitAsync()
    {
        if (done) return;
        var pin = entry.Text ?? "";
        confirm.IsEnabled = false;
        string? message;
        try
        {
            message = await validate(pin);
        }
        finally
        {
            confirm.IsEnabled = true;
        }
        if (message != null)
        {
            problem.Text = message;
            problem.IsVisible = true;
            entry.Text = "";
            entry.Focus();
            return;
        }
        await FinishAsync(pin);
    }

    async Task TryFingerprintAsync()
    {
        if (done || useFingerprint == null) return;
        try
        {
            if (await useFingerprint()) await FinishAsync("fingerprint");
            else entry.Focus();
        }
        catch (Exception ex)
        {
            AppLog.Error("LOCK", "fingerprint check failed", ex);
        }
    }

    async Task FinishAsync(string? value)
    {
        if (done) return;
        done = true;
        try
        {
            if (Navigation.ModalStack.LastOrDefault() == this) await Navigation.PopModalAsync(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("LOCK", "closing the PIN page failed", ex);
        }
        result.TrySetResult(value);
    }

    // The lock screen can not be dismissed with the back button.
    protected override bool OnBackButtonPressed()
    {
        if (allowCancel) _ = FinishAsync(null);
        return true;
    }
}
