using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BtChat;

public sealed record ProxyQrInfo(string Name, IReadOnlyList<string> Addresses, int Port, string? User, string? Password);

// Text inside the proxy QR code:
// btchat://proxy?ip=192.168.43.1&port=8080&name=Pixel[&user=u&pass=p]
public static class ProxyQrPayload
{
    const string Prefix = "btchat://proxy?";

    public static string Build(IEnumerable<string> addresses, int port, string name, string? user = null, string? password = null)
    {
        var sb = new StringBuilder(Prefix);
        sb.Append("ip=").Append(Uri.EscapeDataString(string.Join(",", addresses)));
        sb.Append("&port=").Append(port);
        sb.Append("&name=").Append(Uri.EscapeDataString(name));
        if (!string.IsNullOrEmpty(user))
        {
            sb.Append("&user=").Append(Uri.EscapeDataString(user));
            sb.Append("&pass=").Append(Uri.EscapeDataString(password ?? ""));
        }
        return sb.ToString();
    }

    public static bool TryParse(string? text, out ProxyQrInfo info)
    {
        info = new ProxyQrInfo("", Array.Empty<string>(), 0, null, null);
        if (text == null) return false;
        text = text.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var addresses = new List<string>();
        string name = "", user = "", pass = "";
        var port = 0;
        foreach (var part in text[Prefix.Length..].Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var value = Uri.UnescapeDataString(kv[1]);
            switch (kv[0])
            {
                case "name": name = value; break;
                case "user": user = value; break;
                case "pass": pass = value; break;
                case "port":
                    int.TryParse(value, out port);
                    break;
                case "ip":
                    foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (IPAddress.TryParse(item, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                            addresses.Add(ip.ToString());
                    break;
            }
        }
        if (addresses.Count == 0 || port < 1 || port > 65535) return false;
        info = new ProxyQrInfo(name, addresses, port, user.Length > 0 ? user : null, user.Length > 0 ? pass : null);
        return true;
    }
}

public sealed record ReverseQrInfo(string Name, IReadOnlyList<string> Addresses, int Port, string? Password);

// Text inside the QR code of a receiving device:
// btchat://reverse?ip=192.168.1.5&port=8081&name=PC[&pass=p]
public static class ReverseQrPayload
{
    const string Prefix = "btchat://reverse?";

    public static string Build(IEnumerable<string> addresses, int port, string name, string? password = null)
    {
        var sb = new StringBuilder(Prefix);
        sb.Append("ip=").Append(Uri.EscapeDataString(string.Join(",", addresses)));
        sb.Append("&port=").Append(port);
        sb.Append("&name=").Append(Uri.EscapeDataString(name));
        if (!string.IsNullOrEmpty(password)) sb.Append("&pass=").Append(Uri.EscapeDataString(password));
        return sb.ToString();
    }

    public static bool TryParse(string? text, out ReverseQrInfo info)
    {
        info = new ReverseQrInfo("", Array.Empty<string>(), 0, null);
        if (text == null) return false;
        text = text.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var addresses = new List<string>();
        string name = "", pass = "";
        var port = 0;
        foreach (var part in text[Prefix.Length..].Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var value = Uri.UnescapeDataString(kv[1]);
            switch (kv[0])
            {
                case "name": name = value; break;
                case "pass": pass = value; break;
                case "port": int.TryParse(value, out port); break;
                case "ip":
                    foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (IPAddress.TryParse(item, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                            addresses.Add(ip.ToString());
                    break;
            }
        }
        if (addresses.Count == 0 || port < 1 || port > 65535) return false;
        info = new ReverseQrInfo(name, addresses, port, pass.Length > 0 ? pass : null);
        return true;
    }
}
