using System.Net;
using System.Net.Sockets;
using QRCoder;

namespace BtChat;

public sealed record QrInfo(string Name, IReadOnlyList<string> Addresses);

// Text inside the QR code: btchat://connect?ip=192.168.43.1,10.0.0.5&name=Pixel
public static class QrPayload
{
    const string Prefix = "btchat://connect?";

    public static string Build(IEnumerable<string> addresses, string name) =>
        Prefix + "ip=" + Uri.EscapeDataString(string.Join(",", addresses)) + "&name=" + Uri.EscapeDataString(name);

    public static bool TryParse(string? text, out QrInfo info)
    {
        info = new QrInfo("", Array.Empty<string>());
        if (text == null) return false;
        text = text.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var addresses = new List<string>();
        var name = "";
        foreach (var part in text[Prefix.Length..].Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var value = Uri.UnescapeDataString(kv[1]);
            if (kv[0] == "name") name = value;
            else if (kv[0] == "ip")
            {
                foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (IPAddress.TryParse(item, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                        addresses.Add(ip.ToString());
            }
        }
        if (addresses.Count == 0) return false;
        info = new QrInfo(name, addresses);
        return true;
    }
}

public static class QrImage
{
    public static byte[] Create(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(12);
    }
}

public interface IQrScanner
{
    bool IsSupported { get; }
    // null = the user closed the camera. Throws PermissionException when camera access is denied.
    Task<string?> ScanAsync();
}

#if !ANDROID
public sealed class NoQrScanner : IQrScanner
{
    public bool IsSupported => false;
    public Task<string?> ScanAsync() => Task.FromResult<string?>(null);
}
#endif
