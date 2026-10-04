using System.Text.RegularExpressions;

namespace BtChat;

public sealed partial class MediaViewerPage : ContentPage
{
    const double SwipeDistance = 80;
    const int HoldMs = 1000;
    const float HoldSpeed = 2f;
    const int DoubleTapMs = 320;
    const int SeekStepMs = 10_000;
    const int HideControlsMs = 3500;
    static readonly float[] Speeds = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f };

    readonly ChatMessage message;
    readonly IReceivedFileStore files;
    readonly IReadOnlyList<ChatMessage>? gallery;
    readonly IFileSource? fileSource;
    readonly IReadOnlyList<ChatMessage> siblings;
    readonly MediaPlayerView? player;
    readonly Image? image;
    readonly Label title;
    readonly Button? previous;
    readonly Button? next;
    int index;
    double startScale = 1;
    double startX;
    double startY;

    // player controls
    Grid? controls;
    Button? playButton;
    Button? speedButton;
    Button? subtitleButton;
    Slider? seek;
    Label? currentTime;
    Label? totalTime;
    Border? subtitleBox;
    Label? subtitleLabel;
    Border? hintBox;
    Label? hintLabel;
    IDispatcherTimer? timer;
    CancellationTokenSource? holdCts;
    CancellationTokenSource? tapCts;
    CancellationTokenSource? hintCts;
    SubtitleTrack? track;
    string? shownSubtitle;
    float baseSpeed = 1f;
    long durationMs;
    long subtitleDelayMs;
    long lastTapTicks;
    long lastActivityTicks;
    double touchStartX;
    double touchStartY;
    double subtitleSize = 18;
    bool holding;
    bool touchMoved;
    bool dragging;
    bool ended;
    bool failed;
    bool controlsVisible = true;

    public MediaViewerPage(ChatMessage message, IReceivedFileStore files, IReadOnlyList<ChatMessage>? gallery = null,
        IFileSource? fileSource = null, IEnumerable<ChatMessage>? siblings = null)
    {
        this.message = message;
        this.files = files;
        this.fileSource = fileSource;
        this.siblings = siblings?.ToList() ?? new List<ChatMessage>();
        this.gallery = gallery is { Count: > 1 } ? gallery : null;
        index = this.gallery == null ? 0 : Math.Max(0, this.gallery.ToList().IndexOf(message));
        FlowDirection = Loc.Instance.Flow;
        BackgroundColor = Colors.Black;

        title = new Label
        {
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
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            Padding = new Thickness(12, 8),
            ColumnSpacing = 4
        };
        header.Add(title, 0, 0);

        var body = new Grid();
        if (message.IsImage)
        {
            image = new Image
            {
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

            if (this.gallery != null)
            {
                previous = HeaderButton("‹");
                previous.Clicked += (_, _) => Step(-1);
                next = HeaderButton("›");
                next.Clicked += (_, _) => Step(1);
                header.Add(previous, 3, 0);
                header.Add(next, 4, 0);
            }
        }
        else
        {
            player = new MediaPlayerView
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            body.Add(player);
            player.Touched += OnTouched;
            player.Ended += OnEnded;
            player.Failed += OnFailed;
            player.Prepared += () => ended = false;
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
        if (player != null) BuildPlayerOverlay(body);
        header.Add(openWith, 5, 0);
        header.Add(close, 6, 0);

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
        ShowCurrent();
    }

    ChatMessage Current => gallery != null ? gallery[index] : message;

    static Button HeaderButton(string text) => new()
    {
        Text = text,
        TextColor = Colors.White,
        BackgroundColor = Colors.Transparent,
        FontSize = 20,
        Padding = new Thickness(6, 0),
        WidthRequest = 40,
        HeightRequest = 40,
        MinimumWidthRequest = 0,
        MinimumHeightRequest = 0
    };

    void ShowCurrent()
    {
        var current = Current;
        title.Text = gallery != null ? $"{current.Text}  ({index + 1}/{gallery.Count})" : current.Text;
        if (image != null)
        {
            ResetZoom();
            image.Source = current.Thumb;
        }
        if (previous != null) previous.Opacity = index > 0 ? 1 : 0.3;
        if (next != null && gallery != null) next.Opacity = index < gallery.Count - 1 ? 1 : 0.3;
    }

    void Step(int delta)
    {
        if (gallery == null || image == null) return;
        var target = index + delta;
        if (target < 0 || target >= gallery.Count)
        {
            _ = image.TranslateToAsync(0, 0, 150);
            return;
        }
        index = target;
        ShowCurrent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (player == null) return;
        if (message.Location != null) player.Source = message.Location;
        lastActivityTicks = Environment.TickCount64;
        DeviceDisplay.Current.KeepScreenOn = true;
        timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.Tick += (_, _) => Tick();
        timer.Start();
        if (message.IsVideo) _ = AutoLoadSubtitleAsync();
    }

    protected override void OnDisappearing()
    {
        if (player != null)
        {
            timer?.Stop();
            timer = null;
            holdCts?.Cancel();
            tapCts?.Cancel();
            hintCts?.Cancel();
            DeviceDisplay.Current.KeepScreenOn = false;
            player.Source = null;
            player.Handler?.DisconnectHandler();
        }
        base.OnDisappearing();
    }

    // ---- video / audio player -------------------------------------------------------------------

    void BuildPlayerOverlay(Grid body)
    {
        var loc = Loc.Instance;

        subtitleLabel = new Label
        {
            TextColor = Colors.White,
            FontSize = subtitleSize,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center
        };
        subtitleBox = new Border
        {
            IsVisible = false,
            InputTransparent = true,
            Stroke = Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Color.FromArgb("#99000000"),
            Padding = new Thickness(10, 4),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(12, 0, 12, 104),
            Content = subtitleLabel
        };
        body.Add(subtitleBox);

        hintLabel = new Label { TextColor = Colors.White, FontSize = 18, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center };
        hintBox = new Border
        {
            IsVisible = false,
            InputTransparent = true,
            Stroke = Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Color.FromArgb("#B0000000"),
            Padding = new Thickness(16, 8),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 24, 0, 0),
            Content = hintLabel
        };
        body.Add(hintBox);

        var back = PlayerButton("⏪");
        back.Clicked += (_, _) => { Poke(); SeekBy(-SeekStepMs); };
        playButton = PlayerButton("⏸");
        playButton.FontSize = 26;
        playButton.Clicked += (_, _) => { Poke(); TogglePlay(); };
        var forward = PlayerButton("⏩");
        forward.Clicked += (_, _) => { Poke(); SeekBy(SeekStepMs); };
        speedButton = PlayerButton("1×");
        speedButton.FontSize = 15;
        speedButton.WidthRequest = 56;
        speedButton.Clicked += async (_, _) => { Poke(); await ChooseSpeedAsync(); };
        subtitleButton = PlayerButton("CC");
        subtitleButton.FontSize = 15;
        subtitleButton.Clicked += async (_, _) => { Poke(); await SubtitleMenuAsync(); };

        currentTime = new Label { Text = "0:00", TextColor = Colors.White, FontSize = 12, VerticalOptions = LayoutOptions.Center, WidthRequest = 46, HorizontalTextAlignment = TextAlignment.End };
        totalTime = new Label { Text = "0:00", TextColor = Colors.White, FontSize = 12, VerticalOptions = LayoutOptions.Center, WidthRequest = 46 };
        seek = new Slider { Minimum = 0, Maximum = 1, VerticalOptions = LayoutOptions.Center };
        seek.DragStarted += (_, _) => { dragging = true; Poke(); };
        seek.ValueChanged += (_, e) =>
        {
            if (dragging && currentTime != null) currentTime.Text = FormatTime((long)e.NewValue);
        };
        seek.DragCompleted += (_, _) =>
        {
            dragging = false;
            ended = false;
            player?.SeekTo((long)(seek?.Value ?? 0));
            Poke();
        };

        var timeRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 6
        };
        timeRow.Add(currentTime, 0, 0);
        timeRow.Add(seek, 1, 0);
        timeRow.Add(totalTime, 2, 0);

        var buttonRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 4
        };
        buttonRow.Add(back, 0, 0);
        buttonRow.Add(playButton, 1, 0);
        buttonRow.Add(forward, 2, 0);
        buttonRow.Add(speedButton, 4, 0);
        buttonRow.Add(subtitleButton, 5, 0);

        controls = new Grid
        {
            // Media controls keep the same left-to-right order in every language.
            FlowDirection = FlowDirection.LeftToRight,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
            BackgroundColor = Color.FromArgb("#B3000000"),
            Padding = new Thickness(10, 6, 10, 10),
            VerticalOptions = LayoutOptions.End
        };
        controls.Add(timeRow, 0, 0);
        controls.Add(buttonRow, 0, 1);
        body.Add(controls);
    }

    static Button PlayerButton(string text) => new()
    {
        Text = text,
        TextColor = Colors.White,
        BackgroundColor = Colors.Transparent,
        FontSize = 20,
        Padding = new Thickness(4, 0),
        WidthRequest = 48,
        HeightRequest = 44,
        MinimumWidthRequest = 0,
        MinimumHeightRequest = 0
    };

    static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }

    void Poke() => lastActivityTicks = Environment.TickCount64;

    // Runs every 200 ms while the player is open: position, buttons, subtitle and auto-hiding of the controls.
    void Tick()
    {
        if (player == null || seek == null || failed) return;
        var position = player.GetPosition();
        var duration = player.GetDuration();
        if (duration > 0 && duration != durationMs)
        {
            durationMs = duration;
            seek.Maximum = duration;
            if (totalTime != null) totalTime.Text = FormatTime(duration);
        }
        var playing = player.GetIsPlaying();
        if (!dragging)
        {
            seek.Value = Math.Clamp(position, 0, seek.Maximum);
            if (currentTime != null) currentTime.Text = FormatTime(position);
        }
        if (playButton != null) playButton.Text = playing ? "⏸" : "▶";
        UpdateSubtitle(position);
        if (controlsVisible && playing && !dragging && !holding && Environment.TickCount64 - lastActivityTicks > HideControlsMs)
            SetControlsVisible(false);
    }

    void SetControlsVisible(bool visible)
    {
        controlsVisible = visible;
        if (controls != null) controls.IsVisible = visible;
        if (subtitleBox != null) subtitleBox.Margin = new Thickness(12, 0, 12, visible ? 104 : 24);
        if (visible) Poke();
    }

    void TogglePlay()
    {
        if (player == null || failed) return;
        if (player.GetIsPlaying())
        {
            player.Pause();
            return;
        }
        if (ended)
        {
            player.SeekTo(0);
            ended = false;
        }
        player.Play();
        player.SetSpeed(holding ? HoldSpeed : baseSpeed);
    }

    void SeekBy(long deltaMs)
    {
        if (player == null || failed) return;
        var target = player.GetPosition() + deltaMs;
        var max = player.GetDuration();
        target = Math.Clamp(target, 0, max > 0 ? max : long.MaxValue);
        ended = false;
        player.SeekTo(target);
        ShowHint(deltaMs > 0 ? "+10 s ⏩" : "⏪ −10 s", 700);
    }

    void OnEnded()
    {
        ended = true;
        if (playButton != null) playButton.Text = "▶";
        SetControlsVisible(true);
    }

    void OnFailed(string reason)
    {
        failed = true;
        AppLog.Write("MEDIA", $"player failed: {reason}");
        SetControlsVisible(false);
        ShowHint(Loc.Instance["playFailed"], 0);
    }

    // ---- touch: hold = 2x, tap = controls, double tap = seek / pause -----------------------------

    void OnTouched(PlayerTouch phase, double x, double y)
    {
        switch (phase)
        {
            case PlayerTouch.Down:
                touchStartX = x;
                touchStartY = y;
                touchMoved = false;
                holdCts?.Cancel();
                holdCts = new CancellationTokenSource();
                _ = HoldAsync(holdCts.Token);
                break;
            case PlayerTouch.Move:
                if (!holding && Math.Abs(x - touchStartX) + Math.Abs(y - touchStartY) > 24)
                {
                    touchMoved = true;
                    holdCts?.Cancel();
                }
                break;
            case PlayerTouch.Up:
                holdCts?.Cancel();
                if (holding) EndHold();
                else if (!touchMoved) HandleTap(x);
                break;
            case PlayerTouch.Cancel:
                holdCts?.Cancel();
                if (holding) EndHold();
                break;
        }
    }

    async Task HoldAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(HoldMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (token.IsCancellationRequested || player == null || !player.GetIsPlaying()) return;
            holding = true;
            player.SetSpeed(HoldSpeed);
            ShowHint("⏩ 2×", 0);
        });
    }

    void EndHold()
    {
        holding = false;
        player?.SetSpeed(baseSpeed);
        HideHint();
    }

    void HandleTap(double x)
    {
        var now = Environment.TickCount64;
        if (tapCts != null && now - lastTapTicks < DoubleTapMs)
        {
            tapCts.Cancel();
            tapCts = null;
            var width = player?.Width ?? 0;
            if (width > 0 && x < width / 3) SeekBy(-SeekStepMs);
            else if (width > 0 && x > width * 2 / 3) SeekBy(SeekStepMs);
            else TogglePlay();
            return;
        }
        lastTapTicks = now;
        var cts = tapCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DoubleTapMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!ReferenceEquals(tapCts, cts)) return;
                tapCts = null;
                SetControlsVisible(!controlsVisible);
            });
        });
    }

    // forMs = 0: stays until HideHint.
    void ShowHint(string text, int forMs)
    {
        if (hintBox == null || hintLabel == null) return;
        hintCts?.Cancel();
        hintLabel.Text = text;
        hintBox.IsVisible = true;
        if (forMs <= 0) return;
        var cts = hintCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(forMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!cts.IsCancellationRequested && hintBox != null) hintBox.IsVisible = false;
            });
        });
    }

    void HideHint()
    {
        hintCts?.Cancel();
        if (hintBox != null) hintBox.IsVisible = false;
    }

    // ---- speed -----------------------------------------------------------------------------------

    static string SpeedLabel(float speed) => speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";

    async Task ChooseSpeedAsync()
    {
        var loc = Loc.Instance;
        var labels = Speeds.Select(SpeedLabel).ToArray();
        var picked = await DisplayActionSheet(loc["speed"], loc["cancel"], null, labels);
        var index = picked == null ? -1 : Array.IndexOf(labels, picked);
        if (index < 0) return;
        baseSpeed = Speeds[index];
        if (speedButton != null) speedButton.Text = SpeedLabel(baseSpeed);
        if (!holding) player?.SetSpeed(baseSpeed);
    }

    // ---- subtitles -------------------------------------------------------------------------------

    [GeneratedRegex(@"\s\(\d+\)$")]
    private static partial Regex CopySuffixRegex();

    static string BaseName(string fileName) => CopySuffixRegex().Replace(Path.GetFileNameWithoutExtension(fileName), "");

    // Subtitle files in this chat: the ones named like the video first ("movie.srt", "movie.fa.srt").
    List<ChatMessage> SubtitleCandidates()
    {
        var videoBase = BaseName(message.Text);
        var all = siblings
            .Where(m => m.IsFile && m.Location != null && !m.ShowProgress && !m.Failed && SubtitleTrack.IsSubtitleName(m.Text))
            .ToList();
        bool Matches(ChatMessage m) => videoBase.Length > 0 && BaseName(m.Text).StartsWith(videoBase, StringComparison.OrdinalIgnoreCase);
        return all.OrderByDescending(Matches).Take(6).ToList();
    }

    async Task AutoLoadSubtitleAsync()
    {
        var videoBase = BaseName(message.Text);
        var match = SubtitleCandidates().FirstOrDefault(m => BaseName(m.Text).StartsWith(videoBase, StringComparison.OrdinalIgnoreCase));
        if (match?.Location == null) return;
        AppLog.Write("MEDIA", $"found subtitle next to the video: {match.Text}");
        await LoadSubtitleAsync(match.Text, () => files.OpenReadAsync(match.Location));
    }

    async Task LoadSubtitleAsync(string name, Func<Task<Stream>> open)
    {
        var loc = Loc.Instance;
        try
        {
            await using var input = await open();
            using var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            if (memory.Length > SubtitleTrack.MaxBytes) throw new InvalidDataException("subtitle file too big");
            var parsed = SubtitleTrack.Parse(memory.ToArray());
            if (parsed.Cues.Count == 0)
            {
                ShowHint(loc["subtitleFailed"], 2500);
                return;
            }
            track = parsed;
            subtitleDelayMs = 0;
            shownSubtitle = null;
            SetSubtitleButton(true);
            AppLog.Write("MEDIA", $"subtitle loaded {name} cues={parsed.Cues.Count}");
            ShowHint("CC  " + name, 1800);
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", $"subtitle load failed {name}", ex);
            ShowHint(loc["subtitleFailed"], 2500);
        }
    }

    void SetSubtitleButton(bool on)
    {
        if (subtitleButton != null) subtitleButton.TextColor = on ? Color.FromArgb("#FFD54F") : Colors.White;
    }

    void ClearSubtitle()
    {
        track = null;
        shownSubtitle = null;
        SetSubtitleButton(false);
        if (subtitleBox != null) subtitleBox.IsVisible = false;
    }

    void UpdateSubtitle(long position)
    {
        if (subtitleBox == null || subtitleLabel == null) return;
        var text = track?.TextAt(position - subtitleDelayMs);
        if (text == shownSubtitle) return;
        shownSubtitle = text;
        if (string.IsNullOrEmpty(text))
        {
            subtitleBox.IsVisible = false;
            return;
        }
        subtitleLabel.Text = text;
        // Persian / Arabic lines read right to left.
        subtitleLabel.FlowDirection = text.Any(c => c >= '\u0600' && c <= '\u06FF') ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        subtitleBox.IsVisible = true;
    }

    async Task SubtitleMenuAsync()
    {
        var loc = Loc.Instance;
        var entries = new List<(string Label, Func<Task> Action)>();
        foreach (var candidate in SubtitleCandidates())
        {
            var item = candidate;
            entries.Add(("📄 " + item.Text, () => LoadSubtitleAsync(item.Text, () => files.OpenReadAsync(item.Location!))));
        }
        if (fileSource != null) entries.Add(("📂 " + loc["subtitleChoose"], PickSubtitleAsync));
        if (track != null)
        {
            entries.Add((loc["subtitleEarlier"] + $"  ({FormatDelay()})", () => { subtitleDelayMs -= 500; shownSubtitle = null; return Task.CompletedTask; }));
            entries.Add((loc["subtitleLater"] + $"  ({FormatDelay()})", () => { subtitleDelayMs += 500; shownSubtitle = null; return Task.CompletedTask; }));
            entries.Add((loc["subtitleBigger"], () => { ResizeSubtitle(2); return Task.CompletedTask; }));
            entries.Add((loc["subtitleSmaller"], () => { ResizeSubtitle(-2); return Task.CompletedTask; }));
            entries.Add(("🚫 " + loc["subtitleOff"], () => { ClearSubtitle(); return Task.CompletedTask; }));
        }
        var labels = entries.Select(e => e.Label).ToArray();
        var picked = await DisplayActionSheet(loc["subtitles"], loc["cancel"], null, labels);
        var index = picked == null ? -1 : Array.IndexOf(labels, picked);
        if (index >= 0) await entries[index].Action();
    }

    string FormatDelay() => (subtitleDelayMs / 1000.0).ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";

    void ResizeSubtitle(double delta)
    {
        subtitleSize = Math.Clamp(subtitleSize + delta, 12, 40);
        if (subtitleLabel != null) subtitleLabel.FontSize = subtitleSize;
    }

    async Task PickSubtitleAsync()
    {
        if (fileSource == null) return;
        var loc = Loc.Instance;
        IReadOnlyList<PickedFile> picked;
        try
        {
            picked = await fileSource.PickAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", "subtitle picker failed", ex);
            return;
        }
        var file = picked.FirstOrDefault();
        if (file == null) return;
        try
        {
            if (!SubtitleTrack.IsSubtitleName(file.Name))
            {
                ShowHint(loc["subtitleFailed"], 2500);
                return;
            }
            await LoadSubtitleAsync(file.Name, file.Open);
        }
        finally
        {
            // The picker kept read access to the file; give it back unless a chat message still uses the file.
            foreach (var p in picked)
                if (!siblings.Any(m => m.Location == p.Location))
                    fileSource.Release(p.Location);
        }
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
        var current = Current;
        if (current.Location == null) return;
        try
        {
            await files.OpenAsync(current.Location, current.Text);
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
        if (image == null) return;
        if (image.Scale > 1)
        {
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
            return;
        }
        switch (e.StatusType)
        {
            case GestureStatus.Running:
                if (gallery != null) image.TranslationX = e.TotalX;
                break;
            case GestureStatus.Completed:
                if (gallery != null && Math.Abs(e.TotalX) > SwipeDistance && Math.Abs(e.TotalX) > Math.Abs(e.TotalY))
                    Step(e.TotalX < 0 ? 1 : -1);
                else
                    _ = image.TranslateToAsync(0, 0, 150);
                break;
            case GestureStatus.Canceled:
                image.TranslationX = 0;
                break;
        }
    }
}
