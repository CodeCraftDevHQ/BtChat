using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Webkit;
using Android.Widget;
using AndroidUri = Android.Net.Uri;

namespace BtChat;

public sealed class AndroidReceivedFileStore : IReceivedFileStore
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
        var status = await Permissions.CheckStatusAsync<Permissions.StorageWrite>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.StorageWrite>();
        AppLog.Write("FILES", $"storage permission={status}");
    }

    public Task<ReceivedFile> CreateAsync(string fileName, CancellationToken ct) =>
        Task.FromResult(OperatingSystem.IsAndroidVersionAtLeast(29) ? CreateWithMediaStore(fileName) : CreateLegacy(fileName));

    static ReceivedFile CreateWithMediaStore(string fileName)
    {
        var resolver = Ctx.ContentResolver!;
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
        values.Put(MediaStore.IMediaColumns.MimeType, GetMime(fileName));
        values.Put(MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads + "/" + FolderName);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri!, values)
                  ?? throw new IOException("MediaStore insert failed");
        var stream = resolver.OpenOutputStream(uri) ?? throw new IOException("cannot open output stream");
        var finalName = fileName;
        using (var cursor = resolver.Query(uri, new[] { MediaStore.IMediaColumns.DisplayName }, null, null, null))
        {
            if (cursor != null && cursor.MoveToFirst()) finalName = cursor.GetString(0) ?? fileName;
        }
        return new ReceivedFile
        {
            Stream = stream,
            Name = finalName,
            Location = uri.ToString()!,
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
    }

#pragma warning disable CA1422
    static ReceivedFile CreateLegacy(string fileName)
    {
        var root = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
        var dir = Path.Combine(root, FolderName);
        Directory.CreateDirectory(dir);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var path = Path.Combine(dir, fileName);
        var n = 1;
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
                var finalPath = path;
                return new ReceivedFile
                {
                    Stream = stream,
                    Name = Path.GetFileName(finalPath),
                    Location = finalPath,
                    Complete = () =>
                    {
                        Android.Media.MediaScannerConnection.ScanFile(Ctx, new[] { finalPath }, null, null);
                        return Task.CompletedTask;
                    },
                    Abort = () =>
                    {
                        try { File.Delete(finalPath); } catch { }
                        return Task.CompletedTask;
                    }
                };
            }
            catch (IOException) when (File.Exists(path))
            {
                path = Path.Combine(dir, $"{baseName} ({n++}){ext}");
            }
        }
    }
#pragma warning restore CA1422

    static bool IsContentUri(string location) => location.StartsWith("content://", StringComparison.Ordinal);

    public async Task OpenAsync(string location, string name)
    {
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

    public Task ShowInFolderAsync(string location)
    {
        var folderUri = DocumentsContract.BuildDocumentUri(ExternalStorageAuthority, "primary:" + Android.OS.Environment.DirectoryDownloads + "/" + FolderName);
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
