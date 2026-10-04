using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BtChat;

public sealed record SubtitleCue(long StartMs, long EndMs, string Text);

// Reads SRT and WebVTT subtitle files (own code, no third-party library).
public sealed partial class SubtitleTrack
{
    public const int MaxBytes = 5 * 1024 * 1024;

    readonly List<SubtitleCue> cues;

    SubtitleTrack(List<SubtitleCue> cues) => this.cues = cues;

    public IReadOnlyList<SubtitleCue> Cues => cues;

    public static bool IsSubtitleName(string? name)
    {
        var ext = Path.GetExtension(name ?? "");
        return ext.Equals(".srt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".vtt", StringComparison.OrdinalIgnoreCase);
    }

    // The text that must be on screen at this moment (several overlapping cues are joined), or null.
    public string? TextAt(long ms)
    {
        var lo = 0;
        var hi = cues.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (cues[mid].StartMs <= ms) lo = mid + 1;
            else hi = mid;
        }
        string? result = null;
        // Cues are sorted by start; a long earlier cue can still be showing, so look a few steps back.
        for (var i = Math.Max(0, lo - 6); i < lo; i++)
        {
            if (cues[i].EndMs <= ms) continue;
            result = result == null ? cues[i].Text : result + "\n" + cues[i].Text;
        }
        return result;
    }

    public static SubtitleTrack Parse(byte[] data)
    {
        var text = Decode(data).Replace("\r\n", "\n").Replace('\r', '\n');
        var list = new List<SubtitleCue>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var timing = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timing < 0) continue;
            var match = TimingRegex().Match(lines[timing]);
            if (!match.Success) continue;
            var start = ToMs(match.Groups[1], match.Groups[2], match.Groups[3], match.Groups[4]);
            var end = ToMs(match.Groups[5], match.Groups[6], match.Groups[7], match.Groups[8]);
            var body = string.Join("\n", lines.Skip(timing + 1));
            body = TagRegex().Replace(body, "");
            body = WebUtility.HtmlDecode(body).Trim();
            if (body.Length == 0 || end <= start) continue;
            list.Add(new SubtitleCue(start, end, body));
        }
        list.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        return new SubtitleTrack(list);
    }

    static long ToMs(Group hours, Group minutes, Group seconds, Group fraction)
    {
        var h = hours.Success ? long.Parse(hours.Value) : 0;
        var m = long.Parse(minutes.Value);
        var s = long.Parse(seconds.Value);
        var f = long.Parse(fraction.Value.PadRight(3, '0')[..3]);
        return ((h * 60 + m) * 60 + s) * 1000 + f;
    }

    // UTF-8 (with or without BOM), UTF-16 with BOM, and Windows-1256 for old Persian/Arabic files.
    static string Decode(byte[] d)
    {
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) return Encoding.UTF8.GetString(d, 3, d.Length - 3);
        if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) return Encoding.Unicode.GetString(d, 2, d.Length - 2);
        if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(d, 2, d.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(d);
        }
        catch (DecoderFallbackException)
        {
        }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1256).GetString(d);
        }
        catch
        {
            return Encoding.Latin1.GetString(d);
        }
    }

    [GeneratedRegex(@"(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})")]
    private static partial Regex TimingRegex();

    // <i>, <b>, <c.color>, {\an8} and similar styling marks.
    [GeneratedRegex(@"<[^>]+>|\{\\[^}]*\}")]
    private static partial Regex TagRegex();
}
