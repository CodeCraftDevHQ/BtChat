using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BtChat;

public sealed class WindowsReceivedFileStore : IReceivedFileStore
{
    const string FolderName = "BtChat";
    static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static string DownloadsPath()
    {
        if (SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var ptr) == 0)
        {
            try
            {
                var path = Marshal.PtrToStringUni(ptr);
                if (!string.IsNullOrEmpty(path)) return path;
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    static string TargetDir(string folder) => Path.Combine(DownloadsPath(), FolderName, folder);

    public Task EnsureReadyAsync() => Task.CompletedTask;

    public Task<Stream> OpenReadAsync(string location) => Task.FromResult<Stream>(File.OpenRead(location));

    public Task<ReceivedFile> CreateAsync(string folder, string fileName, CancellationToken ct)
    {
        var dir = TargetDir(folder);
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
                return Task.FromResult(new ReceivedFile
                {
                    Stream = stream,
                    Name = Path.GetFileName(finalPath),
                    Location = finalPath,
                    Complete = () => Task.CompletedTask,
                    Abort = () =>
                    {
                        try { File.Delete(finalPath); } catch { }
                        return Task.CompletedTask;
                    }
                });
            }
            catch (IOException) when (File.Exists(path))
            {
                path = Path.Combine(dir, $"{baseName} ({n++}){ext}");
            }
        }
    }

    static bool CheckExists(string location, string name)
    {
        if (File.Exists(location)) return true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page != null) await page.DisplayAlert(name, Loc.Instance["fileMissing"], "OK");
        });
        return false;
    }

    public Task OpenAsync(string location, string name)
    {
        if (!CheckExists(location, name)) return Task.CompletedTask;
        Process.Start(new ProcessStartInfo(location) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task ShowInFolderAsync(string location)
    {
        if (File.Exists(location))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{location}\"") { UseShellExecute = true });
        else
        {
            var dir = Path.GetDirectoryName(location);
            if (dir != null && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        return Task.CompletedTask;
    }

    public Task ShareAsync(string location, string name) =>
        CheckExists(location, name)
            ? Share.Default.RequestAsync(new ShareFileRequest(name, new ShareFile(location)))
            : Task.CompletedTask;

    public Task DeleteAsync(string location)
    {
        if (File.Exists(location)) File.Delete(location);
        return Task.CompletedTask;
    }
}
