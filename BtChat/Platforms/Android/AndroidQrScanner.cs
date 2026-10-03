namespace BtChat;

public sealed class AndroidQrScanner(IPermissionGate gate) : IQrScanner
{
    public bool IsSupported => true;

    public async Task<string?> ScanAsync()
    {
        if (!await gate.EnsureAsync(PermissionKind.Camera)) throw new PermissionException("camera permission denied");

        var page = Application.Current?.Windows.FirstOrDefault()?.Page
                   ?? throw new InvalidOperationException("no page to show the scanner on");
        var result = new TaskCompletionSource<string?>();
        var scanPage = new QrScanPage(result);
        await page.Navigation.PushModalAsync(scanPage);
        var text = await result.Task;
        try
        {
            if (page.Navigation.ModalStack.Contains(scanPage)) await page.Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("QR", "closing scanner page failed", ex);
        }
        return text;
    }
}
