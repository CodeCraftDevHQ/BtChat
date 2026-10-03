using Android.Widget;
using Microsoft.Maui.Handlers;
using AndroidUri = Android.Net.Uri;

namespace BtChat;

public sealed class MediaPlayerViewHandler : ViewHandler<MediaPlayerView, VideoView>
{
    public static readonly IPropertyMapper<MediaPlayerView, MediaPlayerViewHandler> PropertyMapper =
        new PropertyMapper<MediaPlayerView, MediaPlayerViewHandler>(ViewHandler.ViewMapper)
        {
            [nameof(MediaPlayerView.Source)] = MapSource
        };

    public MediaPlayerViewHandler() : base(PropertyMapper)
    {
    }

    protected override VideoView CreatePlatformView()
    {
        var view = new VideoView(Context);
        var controller = new MediaController(Context);
        controller.SetAnchorView(view);
        view.SetMediaController(controller);
        return view;
    }

    protected override void DisconnectHandler(VideoView platformView)
    {
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
