using Android.Media;
using Android.Views;
using Android.Widget;
using Microsoft.Maui.Handlers;
using AndroidUri = Android.Net.Uri;

namespace BtChat;

public sealed class MediaPlayerViewHandler : ViewHandler<MediaPlayerView, VideoView>, IPlayerBackend
{
    public static readonly IPropertyMapper<MediaPlayerView, MediaPlayerViewHandler> PropertyMapper =
        new PropertyMapper<MediaPlayerView, MediaPlayerViewHandler>(ViewHandler.ViewMapper)
        {
            [nameof(MediaPlayerView.Source)] = MapSource
        };

    MediaPlayer? mediaPlayer;
    float speed = 1f;

    public MediaPlayerViewHandler() : base(PropertyMapper)
    {
    }

    // No system MediaController: the shared player screen draws its own controls.
    protected override VideoView CreatePlatformView() => new(Context);

    protected override void ConnectHandler(VideoView platformView)
    {
        base.ConnectHandler(platformView);
        VirtualView.Backend = this;
        platformView.SetOnPreparedListener(new PreparedListener(this));
        platformView.Completion += OnCompletion;
        platformView.Error += OnError;
        platformView.Touch += OnTouch;
    }

    protected override void DisconnectHandler(VideoView platformView)
    {
        platformView.SetOnPreparedListener(null);
        platformView.Completion -= OnCompletion;
        platformView.Error -= OnError;
        platformView.Touch -= OnTouch;
        VirtualView.Backend = null;
        mediaPlayer = null;
        try
        {
            platformView.StopPlayback();
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", "stop playback failed", ex);
        }
        base.DisconnectHandler(platformView);
    }

    sealed class PreparedListener(MediaPlayerViewHandler owner) : Java.Lang.Object, MediaPlayer.IOnPreparedListener
    {
        public void OnPrepared(MediaPlayer? mp) => owner.OnPrepared(mp);
    }

    void OnPrepared(MediaPlayer? mp)
    {
        mediaPlayer = mp;
        AppLog.Write("MEDIA", $"video prepared duration={PlatformView.Duration}ms");
        VirtualView.RaisePrepared();
    }

    void OnCompletion(object? sender, EventArgs e) => VirtualView.RaiseEnded();

    void OnError(object? sender, MediaPlayer.ErrorEventArgs e)
    {
        // Handled: otherwise the system shows its own "can't play this video" dialog.
        e.Handled = true;
        AppLog.Write("MEDIA", $"video error what={e.What} extra={e.Extra}");
        VirtualView.RaiseFailed($"{e.What}/{e.Extra}");
    }

    void OnTouch(object? sender, Android.Views.View.TouchEventArgs e)
    {
        var motion = e.Event;
        if (motion == null) return;
        var density = Context.Resources?.DisplayMetrics?.Density ?? 1f;
        PlayerTouch? phase = motion.ActionMasked switch
        {
            MotionEventActions.Down => PlayerTouch.Down,
            MotionEventActions.Move => PlayerTouch.Move,
            MotionEventActions.Up => PlayerTouch.Up,
            MotionEventActions.Cancel => PlayerTouch.Cancel,
            _ => null
        };
        if (phase != null) VirtualView.RaiseTouch(phase.Value, motion.GetX() / density, motion.GetY() / density);
        // Returning true keeps the following move / up events coming to us.
        e.Handled = true;
    }

    public long Position => PlatformView.CurrentPosition;

    public long Duration => Math.Max(0, PlatformView.Duration);

    public bool IsPlaying => PlatformView.IsPlaying;

    public void Play()
    {
        PlatformView.Start();
        ApplySpeed();
    }

    public void Pause() => PlatformView.Pause();

    public void SeekTo(long ms) => PlatformView.SeekTo((int)Math.Clamp(ms, 0, int.MaxValue));

    public void SetSpeed(float value)
    {
        speed = value;
        ApplySpeed();
    }

    // Time-stretching keeps the voice pitch normal. Only while playing: on some Android versions
    // setting the speed of a paused player makes it start.
    void ApplySpeed()
    {
        var player = mediaPlayer;
        if (player == null || !PlatformView.IsPlaying) return;
        try
        {
            var parameters = player.PlaybackParams;
            parameters.SetSpeed(speed);
            player.PlaybackParams = parameters;
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
            player.StopPlayback();
            return;
        }
        try
        {
            if (source.StartsWith("content://", StringComparison.Ordinal))
                player.SetVideoURI(AndroidUri.Parse(source));
            else
                player.SetVideoPath(source);
            player.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", "set source failed", ex);
        }
    }
}
