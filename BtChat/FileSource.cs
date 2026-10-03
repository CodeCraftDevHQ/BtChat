namespace BtChat;

// A file chosen for sending. Location is the ORIGINAL file (path or content:// uri): no copy is made.
public sealed record PickedFile(string Name, string Location, long Size, Func<Task<Stream>> Open);

public interface IFileSource
{
    Task<IReadOnlyList<PickedFile>> PickAsync();
    // Forget the saved access to a file that is no longer shown in the chat.
    void Release(string location);
}

#if !ANDROID
public sealed class DefaultFileSource : IFileSource
{
    public async Task<IReadOnlyList<PickedFile>> PickAsync()
    {
        var picked = await FilePicker.Default.PickMultipleAsync();
        var list = new List<PickedFile>();
        foreach (var file in picked ?? Array.Empty<FileResult>())
        {
            long size = -1;
            try
            {
                var info = new FileInfo(file.FullPath);
                if (info.Exists) size = info.Length;
            }
            catch
            {
            }
            var captured = file;
            list.Add(new PickedFile(file.FileName, file.FullPath, size, () => captured.OpenReadAsync()));
        }
        return list;
    }

    public void Release(string location)
    {
    }
}
#endif
