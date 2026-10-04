using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Playback;

namespace BtChat;

public sealed class MediaPlayerViewHandler : ViewHandler<MediaPlayerView, MediaPlayerElement>, IPlayerBackend
{
    public static readonly IPropertyMapper<MediaPlayerView, MediaPlayerViewHandler> PropertyMapper =
        new PropertyMapper<MediaPlayerView, MediaPlayerViewHandler>(ViewHandler.ViewMapper)
        {
            [nameof(MediaPlayerView.Source)] = MapSource
        };

    MediaPlayerView? view;
    float speed = 1f;

    public MediaPlayerViewHandler() : base(PropertyMapper)
    {
    }

    protected override MediaPlayerElement CreatePlatformView()
    {
        var element = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            AutoPlay = true,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
        };
        element.SetMediaPlayer(new MediaPlayer());
        return element;
    }

    protected override void ConnectHandler(MediaPlayerElement platformView)
    {
        base.ConnectHandler(platformView);
        view = VirtualView;
        view.Backend = this;
        var player = platformView.MediaPlayer;
        if (player != null)
        {
            player.MediaOpened += OnOpened;
            player.MediaEnded += OnEnded;
            player.MediaFailed += OnFailed;
        }
        // handledEventsToo: the element itself may mark pointer events as handled.
        platformView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        platformView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        platformView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        platformView.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnCanceled), true);
        platformView.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCanceled), true);
    }

    protected override void DisconnectHandler(MediaPlayerElement platformView)
    {
        platformView.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed));
        platformView.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved));
        platformView.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased));
        platformView.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnCanceled));
        platformView.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCanceled));
        try
        {
            var player = platformView.MediaPlayer;
            if (player != null)
            {
                player.MediaOpened -= OnOpened;
                player.MediaEnded -= OnEnded;
                player.MediaFailed -= OnFailed;
                player.Pause();
            }
            platformView.Source = null;
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", "stop playback failed", ex);
        }
        if (view != null) view.Backend = null;
        view = null;
        base.DisconnectHandler(platformView);
    }

    // Media events arrive on a background thread.
    void OnOpened(MediaPlayer sender, object args)
    {
        var target = view;
        MainThread.BeginInvokeOnMainThread(() => target?.RaisePrepared());
    }

    void OnEnded(MediaPlayer sender, object args)
    {
        var target = view;
        MainThread.BeginInvokeOnMainThread(() => target?.RaiseEnded());
    }

    void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var target = view;
        var reason = args.ErrorMessage;
        AppLog.Write("MEDIA", $"video error {args.Error} {reason}");
        MainThread.BeginInvokeOnMainThread(() => target?.RaiseFailed(reason));
    }

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        PlatformView.CapturePointer(e.Pointer);
        Raise(PlayerTouch.Down, e);
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(PlatformView).IsInContact) Raise(PlayerTouch.Move, e);
    }

    void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        Raise(PlayerTouch.Up, e);
        PlatformView.ReleasePointerCapture(e.Pointer);
    }

    void OnCanceled(object sender, PointerRoutedEventArgs e) => Raise(PlayerTouch.Cancel, e);

    void Raise(PlayerTouch phase, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PlatformView).Position;
        view?.RaiseTouch(phase, point.X, point.Y);
    }

    Windows.Media.Playback.MediaPlaybackSession? Session => PlatformView.MediaPlayer?.PlaybackSession;

    public long Position => (long)(Session?.Position.TotalMilliseconds ?? 0);

    public long Duration => (long)(Session?.NaturalDuration.TotalMilliseconds ?? 0);

    public bool IsPlaying => Session?.PlaybackState == MediaPlaybackState.Playing;

    public void Play()
    {
        PlatformView.MediaPlayer?.Play();
        ApplySpeed();
    }

    public void Pause() => PlatformView.MediaPlayer?.Pause();

    public void SeekTo(long ms)
    {
        var session = Session;
        if (session != null) session.Position = TimeSpan.FromMilliseconds(Math.Max(0, ms));
    }

    public void SetSpeed(float value)
    {
        speed = value;
        ApplySpeed();
    }

    void ApplySpeed()
    {
        var session = Session;
        if (session == null) return;
        try
        {
            session.PlaybackRate = speed;
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", $"set speed {speed} failed", ex);
        }
    }

    static void MapSource(MediaPlayerViewHandler handler, MediaPlayerView view)
    {
        var source = view.Source;
        var player = handler.PlatformView;
        if (string.IsNullOrEmpty(source))
        {
            player.Source = null;
            return;
        }
        try
        {
            player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(source));
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", "set source failed", ex);
        }
    }
}
