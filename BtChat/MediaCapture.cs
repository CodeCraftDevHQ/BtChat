namespace BtChat;

public static class MediaCapture
{
    // Opens the system camera for one photo or video and keeps a private copy of the result.
    public static async Task<(string Name, string Path, long Size)?> CaptureAsync(bool video)
    {
        if (!MediaPicker.Default.IsCaptureSupported) return null;
        var result = video ? await MediaPicker.Default.CaptureVideoAsync() : await MediaPicker.Default.CapturePhotoAsync();
        if (result == null) return null;
        var folder = Path.Combine(FileSystem.AppDataDirectory, "captures");
        Directory.CreateDirectory(folder);
        var extension = Path.GetExtension(result.FileName);
        if (string.IsNullOrEmpty(extension)) extension = video ? ".mp4" : ".jpg";
        var name = $"{(video ? "video" : "photo")}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
        var target = Path.Combine(folder, name);
        await using (var input = await result.OpenReadAsync())
        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
        {
            await input.CopyToAsync(output);
        }
        try
        {
            if (!string.IsNullOrEmpty(result.FullPath) && File.Exists(result.FullPath)) File.Delete(result.FullPath);
        }
        catch
        {
        }
        return (name, target, new FileInfo(target).Length);
    }
}
