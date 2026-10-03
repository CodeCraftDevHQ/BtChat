using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Webkit;
using Android.Widget;
using Microsoft.Win32.SafeHandles;
using AndroidUri = Android.Net.Uri;

namespace BtChat;

public sealed class AndroidReceivedFileStore(IPermissionGate gate) : IReceivedFileStore
{
    const string FolderName = "BtChat";
    const string ExternalStorageAuthority = "com.android.externalstorage.documents";

    static Context Ctx => Android.App.Application.Context;

    static string GetMime(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext) ?? "application/octet-stream";
    }

    static void ShowToast(string text) =>
        MainThread.BeginInvokeOnMainThread(() => Android.Widget.Toast.MakeText(Ctx, text, ToastLength.Long)?.Show());

    public async Task EnsureReadyAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) return;
        var granted = await gate.EnsureAsync(PermissionKind.Storage);
        AppLog.Write("FILES", $"storage permission granted={granted}");
    }

    public Task<byte[]?> GetVideoThumbnailAsync(string location) => Task.Run<byte[]?>(() =>
    {
        var retriever = new Android.Media.MediaMetadataRetriever();
        try
        {
            if (IsContentUri(location)) retriever.SetDataSource(Ctx, AndroidUri.Parse(location)!);
            else retriever.SetDataSource(location);
            using var frame = retriever.GetFrameAtTime(1_000_000, Android.Media.Option.ClosestSync);
            if (frame == null) return null;
            var width = 480;
            var height = Math.Max(1, frame.Height * width / Math.Max(1, frame.Width));
            using var scaled = Android.Graphics.Bitmap.CreateScaledBitmap(frame, width, height, true)!;
            using var output = new MemoryStream();
            scaled.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 80, output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Error("MEDIA", "video thumbnail failed", ex);
            return null;
        }
        finally
        {
            retriever.Release();
        }
    });

    public Task<Stream> OpenReadAsync(string location)
    {
        if (!IsContentUri(location)) return Task.FromResult<Stream>(File.OpenRead(location));
        var stream = Ctx.ContentResolver!.OpenInputStream(AndroidUri.Parse(location)!) ?? throw new IOException("cannot open " + location);
        return Task.FromResult<Stream>(stream);
    }

    public Task<ReceivedFile> CreateAsync(string folder, string fileName, CancellationToken ct) =>
        Task.FromResult(OperatingSystem.IsAndroidVersionAtLeast(29) ? CreateWithMediaStore(folder, fileName) : CreateLegacy(folder, fileName));

    // Opens the file behind a content:// uri for writing through a native file descriptor: no Java stream
    // in between (faster) and it can seek, which continuing a partial file needs. Null if the provider refuses.
    static FileStream? OpenWriteFd(AndroidUri uri)
    {
        SafeFileHandle? handle = null;
        try
        {
            using var descriptor = Ctx.ContentResolver!.OpenFileDescriptor(uri, "rw");
            if (descriptor == null) return null;
            handle = new SafeFileHandle((IntPtr)descriptor.DetachFd(), true);
            return new FileStream(handle, FileAccess.Write, 1, false);
        }
        catch (Exception ex)
        {
            handle?.Dispose();
            AppLog.Error("FILES", $"native write open failed for {uri}, using a stream", ex);
            return null;
        }
    }

    static string? DisplayNameOf(AndroidUri uri)
    {
        using var cursor = Ctx.ContentResolver!.Query(uri, new[] { MediaStore.IMediaColumns.DisplayName }, null, null, null);
        return cursor != null && cursor.MoveToFirst() ? cursor.GetString(0) : null;
    }

    static ReceivedFile CreateWithMediaStore(string folder, string fileName)
    {
        var resolver = Ctx.ContentResolver!;
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
        values.Put(MediaStore.IMediaColumns.MimeType, GetMime(fileName));
        values.Put(MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads + "/" + FolderName + "/" + folder);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri!, values)
                  ?? throw new IOException("MediaStore insert failed");
        Stream? stream = OpenWriteFd(uri);
        stream ??= resolver.OpenOutputStream(uri);
        if (stream == null) throw new IOException("cannot open output stream");
        return MediaStoreFile(resolver, uri, stream, DisplayNameOf(uri) ?? fileName, 0);
    }

    static ReceivedFile MediaStoreFile(ContentResolver resolver, AndroidUri uri, Stream stream, string name, long existing) => new()
    {
        Stream = stream,
        Name = name,
        Location = uri.ToString()!,
        ExistingLength = existing,
        Complete = () =>
        {
            var done = new ContentValues();
            done.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, done, null, null);
            return Task.CompletedTask;
        },
        Abort = () =>
        {
            resolver.Delete(uri, null, null);
            return Task.CompletedTask;
        }
    };

    public Task<ReceivedFile?> OpenForResumeAsync(string location, CancellationToken ct) =>
        Task.FromResult(IsContentUri(location) ? ResumeMediaStore(location) : ResumeLegacy(location));

    static ReceivedFile? ResumeMediaStore(string location)
    {
        var uri = AndroidUri.Parse(location)!;
        var stream = OpenWriteFd(uri);
        if (stream == null) return null;
        if (!stream.CanSeek)
        {
            stream.Dispose();
            return null;
        }
        var existing = stream.Length;
        stream.Seek(0, SeekOrigin.End);
        string? name = null;
        try { name = DisplayNameOf(uri); }
        catch (Exception ex) { AppLog.Error("FILES", "display name query failed", ex); }
        return MediaStoreFile(Ctx.ContentResolver!, uri, stream, name ?? "file", existing);
    }

    static ReceivedFile? ResumeLegacy(string path)
    {
        if (!File.Exists(path)) return null;
        var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
        var existing = stream.Length;
        stream.Seek(0, SeekOrigin.End);
        return LegacyFile(stream, path, existing);
    }

    static ReceivedFile LegacyFile(FileStream stream, string path, long existing) => new()
    {
        Stream = stream,
        Name = Path.GetFileName(path),
        Location = path,
        ExistingLength = existing,
        Complete = () =>
        {
            Android.Media.MediaScannerConnection.ScanFile(Ctx, new[] { path }, null, null);
            return Task.CompletedTask;
        },
        Abort = () =>
        {
            try { File.Delete(path); } catch { }
            return Task.CompletedTask;
        }
    };

#pragma warning disable CA1422
    static ReceivedFile CreateLegacy(string folder, string fileName)
    {
        var root = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
        var dir = Path.Combine(root, FolderName, folder);
        Directory.CreateDirectory(dir);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var path = Path.Combine(dir, fileName);
        var n = 1;
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
                return LegacyFile(stream, path, 0);
            }
            catch (IOException) when (File.Exists(path))
            {
                path = Path.Combine(dir, $"{baseName} ({n++}){ext}");
            }
        }
    }
#pragma warning restore CA1422

    static bool IsContentUri(string location) => location.StartsWith("content://", StringComparison.Ordinal);

    // The original file may have been moved or deleted since it was sent or received.
    static bool Exists(string location)
    {
        try
        {
            if (!IsContentUri(location)) return File.Exists(location);
            using var fd = Ctx.ContentResolver!.OpenAssetFileDescriptor(AndroidUri.Parse(location)!, "r");
            return fd != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task OpenAsync(string location, string name)
    {
        if (!Exists(location))
        {
            ShowToast(Loc.Instance["fileMissing"]);
            return;
        }
        if (!IsContentUri(location))
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest(name, new ReadOnlyFile(location)));
            return;
        }
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(AndroidUri.Parse(location), GetMime(name));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
        try
        {
            Ctx.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            ShowToast(Loc.Instance["noApp"]);
        }
    }

#pragma warning disable CA1422
    static string? FolderOf(string location)
    {
        try
        {
            if (IsContentUri(location))
            {
                using var cursor = Ctx.ContentResolver!.Query(AndroidUri.Parse(location)!, new[] { MediaStore.IMediaColumns.RelativePath }, null, null, null);
                if (cursor != null && cursor.MoveToFirst())
                {
                    var relativePath = cursor.GetString(0)?.Trim('/');
                    return string.IsNullOrEmpty(relativePath) ? null : relativePath;
                }
                return null;
            }
            var dir = Path.GetDirectoryName(location);
            var downloads = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
            if (dir != null && dir.StartsWith(downloads, StringComparison.Ordinal))
                return Android.OS.Environment.DirectoryDownloads + dir[downloads.Length..].Replace('\\', '/');
        }
        catch
        {
        }
        return null;
    }
#pragma warning restore CA1422

    public Task ShowInFolderAsync(string location)
    {
        var relative = FolderOf(location) ?? Android.OS.Environment.DirectoryDownloads + "/" + FolderName;
        var folderUri = DocumentsContract.BuildDocumentUri(ExternalStorageAuthority, "primary:" + relative);
        var view = new Intent(Intent.ActionView);
        view.SetDataAndType(folderUri, DocumentsContract.Document.MimeTypeDir);
        view.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
        try
        {
            Ctx.StartActivity(view);
            return Task.CompletedTask;
        }
        catch (ActivityNotFoundException)
        {
        }
        catch (Java.Lang.SecurityException)
        {
        }
        try
        {
            var downloads = new Intent(Android.App.DownloadManager.ActionViewDownloads);
            downloads.AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(downloads);
        }
        catch (ActivityNotFoundException)
        {
        }
        ShowToast(Loc.Instance["savedIn"] + " Download/" + FolderName);
        return Task.CompletedTask;
    }

    public async Task ShareAsync(string location, string name)
    {
        if (!Exists(location))
        {
            ShowToast(Loc.Instance["fileMissing"]);
            return;
        }
        if (!IsContentUri(location))
        {
            await Share.Default.RequestAsync(new ShareFileRequest(name, new ShareFile(location)));
            return;
        }
        var send = new Intent(Intent.ActionSend);
        send.SetType(GetMime(name));
        send.PutExtra(Intent.ExtraStream, AndroidUri.Parse(location));
        send.AddFlags(ActivityFlags.GrantReadUriPermission);
        var chooser = Intent.CreateChooser(send, name)!;
        chooser.AddFlags(ActivityFlags.NewTask);
        Ctx.StartActivity(chooser);
    }

    public Task DeleteAsync(string location)
    {
        if (IsContentUri(location))
        {
            Ctx.ContentResolver!.Delete(AndroidUri.Parse(location)!, null, null);
        }
        else if (File.Exists(location))
        {
            File.Delete(location);
            Android.Media.MediaScannerConnection.ScanFile(Ctx, new[] { location }, null, null);
        }
        return Task.CompletedTask;
    }
}
