namespace BtChat;

// Private copies of files shared while no device is connected. The original uri of a shared file only
// stays readable for a short time, so a file that has to wait for a connection is copied into the app.
public static class OutgoingStore
{
    static string Root => Path.Combine(FileSystem.AppDataDirectory, "outgoing");

    public static bool IsOurs(string? location)
    {
        if (string.IsNullOrEmpty(location) || location.StartsWith("content://", StringComparison.Ordinal)) return false;
        try
        {
            return Path.GetFullPath(location).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // Returns the path of the copy. Every copy gets its own folder, so equal file names never clash.
    public static async Task<string> CopyAsync(string name, string location, Func<string, Task<Stream>> open, CancellationToken ct)
    {
        var folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var safe = Path.GetFileName(name);
        foreach (var bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
        if (string.IsNullOrWhiteSpace(safe)) safe = "file";
        var target = Path.Combine(folder, safe);
        try
        {
            await using var input = await open(location);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            await input.CopyToAsync(output, 256 * 1024, ct);
            return target;
        }
        catch
        {
            TryDeleteFolder(folder);
            throw;
        }
    }

    // Removes a copy (and its folder). Anything that is not one of our copies is left alone.
    public static void Delete(string? location)
    {
        if (!IsOurs(location)) return;
        var folder = Path.GetDirectoryName(Path.GetFullPath(location!));
        if (folder != null && folder.Length > Root.Length) TryDeleteFolder(folder);
    }

    static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception ex)
        {
            AppLog.Error("FILES", $"delete outgoing copy failed {folder}", ex);
        }
    }
}
