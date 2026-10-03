using System.Diagnostics;
using System.Text;

namespace BtChat;

public static class AppLog
{
    const int MaxLines = 1000;
    const int TrimToLines = 800;
    const int MaxLineLength = 400;

    static readonly object gate = new();
    static readonly List<string> lines = new();

#if DEBUG
    public static bool VerboseEnabled { get; set; } = true;
#else
    public static bool VerboseEnabled { get; set; } = false;
#endif

    public static event Action? Changed;

    public static void Verbose(string tag, string message)
    {
        if (VerboseEnabled) Write(tag, message);
    }

    public static void Write(string tag, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} T{Environment.CurrentManagedThreadId} {tag}: {message}";
        if (line.Length > MaxLineLength) line = line[..MaxLineLength] + "…";
        lock (gate)
        {
            lines.Add(line);
            if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - TrimToLines);
        }
        Debug.WriteLine(line);
        Changed?.Invoke();
    }

    public static void Error(string tag, string message, Exception ex) =>
        Write(tag, $"{message} -> {Describe(ex)}");

    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" <- ");
            sb.Append($"{e.GetType().FullName}: {e.Message} (0x{e.HResult:X8})");
        }
        return sb.ToString();
    }

    public static string Caller(int skip = 2, int depth = 6)
    {
        var frames = new StackTrace(skip, false).GetFrames();
        return string.Join(" < ", frames.Take(depth).Select(f =>
        {
            var m = f.GetMethod();
            return $"{m?.DeclaringType?.Name}.{m?.Name}";
        }));
    }

    public static string GetText(int lastLines = int.MaxValue)
    {
        lock (gate)
        {
            var skip = Math.Max(0, lines.Count - lastLines);
            return string.Join(Environment.NewLine, lines.Skip(skip));
        }
    }

    public static void Clear()
    {
        lock (gate)
        {
            lines.Clear();
        }
        Changed?.Invoke();
    }
}
