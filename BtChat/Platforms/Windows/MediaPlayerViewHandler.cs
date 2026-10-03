using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;

namespace BtChat;

public sealed class MediaPlayerViewHandler : ViewHandler<MediaPlayerView, MediaPlayerElement>
{
    public static readonly IPropertyMapper<MediaPlayerView, MediaPlayerViewHandler> PropertyMapper =
        new PropertyMapper<MediaPlayerView, MediaPlayerViewHandler>(ViewHandler.ViewMapper)
        {
            [nameof(MediaPlayerView.Source)] = MapSource
        };

    public MediaPlayerViewHandler() : base(PropertyMapper)
    {
    }

    protected override MediaPlayerElement CreatePlatformView() => new()
    {
        AreTransportControlsEnabled = true,
        AutoPlay = true,
        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
    };

    protected override void DisconnectHandler(MediaPlayerElement platformView)
    {
        try
        {
            platformView.MediaPlayer?.Pause();
            platformView.Source = null;
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", "stop playback failed", ex);
        }
        base.DisconnectHandler(platformView);
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
