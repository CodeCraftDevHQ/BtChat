using System.Text;

namespace BtChat;

// Writes unhandled errors to a file before the app dies, and shows them in the log on the next start.
public static class CrashReport
{
    static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "last-crash.txt");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Save("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Save("Task", e.Exception);
#if ANDROID
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Save("Android", e.Exception);
#endif
    }

    static void Save(string source, Exception? ex)
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {source}");
            text.AppendLine(ex?.ToString() ?? "unknown error");
            text.AppendLine("--- last log lines ---");
            text.AppendLine(AppLog.GetText(60));
            File.WriteAllText(FilePath, text.ToString());
        }
        catch
        {
        }
    }

    public static void ReportPrevious()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var text = File.ReadAllText(FilePath);
            File.Delete(FilePath);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0) AppLog.Write("CRASH", trimmed);
            }
        }
        catch
        {
        }
    }
}
