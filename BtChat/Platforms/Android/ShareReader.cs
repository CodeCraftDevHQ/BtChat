using Android.Content;
using AndroidUri = Android.Net.Uri;

namespace BtChat;

// Turns an Android "share" intent (ACTION_SEND / ACTION_SEND_MULTIPLE) into a list of files.
public static class ShareReader
{
    public static List<SharedFile> Read(Intent intent)
    {
        var uris = new List<AndroidUri>();
        // The system copies the shared streams into ClipData, together with the permission to read them.
        if (intent.ClipData is { } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++)
            {
                if (clip.GetItemAt(i)?.Uri is { } uri) uris.Add(uri);
            }
        }
        if (uris.Count == 0)
        {
#pragma warning disable CA1422
            if (intent.GetParcelableExtra(Intent.ExtraStream) is AndroidUri single) uris.Add(single);
#pragma warning restore CA1422
        }

        var resolver = Android.App.Application.Context.ContentResolver!;
        var list = new List<SharedFile>();
        foreach (var uri in uris)
        {
            if (string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                var path = uri.Path;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                list.Add(new SharedFile(Path.GetFileName(path), path, new FileInfo(path).Length));
                continue;
            }
            var (name, size) = AndroidFileSource.Describe(resolver, uri);
            AppLog.Write("SHARE", $"shared {name} size={size} uri={uri}");
            list.Add(new SharedFile(name, uri.ToString()!, size));
        }
        return list;
    }
}
