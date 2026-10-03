using Android.Content;
using Microsoft.Win32.SafeHandles;
using AndroidUri = Android.Net.Uri;

namespace BtChat;

// Uses the system document picker so we get the original content:// uri and a permission that survives restarts.
public sealed class AndroidFileSource : IFileSource
{
    public async Task<IReadOnlyList<PickedFile>> PickAsync()
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("no activity");
        var result = new TaskCompletionSource<Intent?>();
        MainActivity.PickResult = result;
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        activity.StartActivityForResult(intent, MainActivity.PickFilesRequest);
        var data = await result.Task;
        MainActivity.PickResult = null;

        var uris = new List<AndroidUri>();
        if (data?.ClipData is { } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++)
            {
                var uri = clip.GetItemAt(i)?.Uri;
                if (uri != null) uris.Add(uri);
            }
        }
        else if (data?.Data is { } single)
        {
            uris.Add(single);
        }

        var resolver = activity.ContentResolver!;
        var list = new List<PickedFile>();
        foreach (var uri in uris)
        {
            try
            {
                resolver.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
            }
            catch (Exception ex)
            {
                // Some providers do not allow it: the file still works until the app is restarted.
                AppLog.Error("FILES", $"persistable permission refused for {uri}", ex);
            }
            var name = "file";
            long size = -1;
            try
            {
                using var cursor = resolver.Query(uri, new[] { "_display_name", "_size" }, null, null, null);
                if (cursor != null && cursor.MoveToFirst())
                {
                    var n = cursor.GetColumnIndex("_display_name");
                    if (n >= 0 && !cursor.IsNull(n)) name = cursor.GetString(n) ?? name;
                    var z = cursor.GetColumnIndex("_size");
                    if (z >= 0 && !cursor.IsNull(z)) size = cursor.GetLong(z);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("FILES", $"query failed for {uri}", ex);
            }
            var location = uri.ToString()!;
            AppLog.Write("FILES", $"picked {name} size={size} uri={location}");
            list.Add(new PickedFile(name, location, size, () => OpenAsync(location)));
        }
        return list;
    }

    // Reads through the native file descriptor when the provider gives one: no Java stream in between (faster),
    // and the file can seek, so a transfer can continue from the middle. Other providers use a normal stream.
    public Task<Stream> OpenAsync(string location)
    {
        if (!location.StartsWith("content://", StringComparison.Ordinal))
            return Task.FromResult<Stream>(new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1));
        var resolver = Android.App.Application.Context.ContentResolver!;
        var uri = AndroidUri.Parse(location)!;
        SafeFileHandle? handle = null;
        try
        {
            using var descriptor = resolver.OpenFileDescriptor(uri, "r");
            if (descriptor != null)
            {
                handle = new SafeFileHandle((IntPtr)descriptor.DetachFd(), true);
                return Task.FromResult<Stream>(new FileStream(handle, FileAccess.Read, 1, false));
            }
        }
        catch (Exception ex)
        {
            handle?.Dispose();
            AppLog.Error("FILES", $"native open failed for {location}, using a stream", ex);
        }
        var stream = resolver.OpenInputStream(uri) ?? throw new IOException("cannot open " + location);
        return Task.FromResult<Stream>(stream);
    }

    public void Release(string location)
    {
        if (!location.StartsWith("content://", StringComparison.Ordinal)) return;
        try
        {
            Android.App.Application.Context.ContentResolver!
                .ReleasePersistableUriPermission(AndroidUri.Parse(location)!, ActivityFlags.GrantReadUriPermission);
            AppLog.Write("FILES", $"released permission {location}");
        }
        catch
        {
            // It was never persisted or is already released.
        }
    }
}
