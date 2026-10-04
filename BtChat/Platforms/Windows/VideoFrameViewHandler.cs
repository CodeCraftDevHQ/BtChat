using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;

namespace BtChat;

// Video calls are Android only; this empty handler just keeps the shared page working on Windows.
public sealed class VideoFrameViewHandler : ViewHandler<VideoFrameView, Grid>
{
    public static readonly IPropertyMapper<VideoFrameView, VideoFrameViewHandler> PropertyMapper =
        new PropertyMapper<VideoFrameView, VideoFrameViewHandler>(ViewHandler.ViewMapper);

    public VideoFrameViewHandler() : base(PropertyMapper)
    {
    }

    protected override Grid CreatePlatformView() => new();
}
