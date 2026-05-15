using System.IO;

namespace RemotePlay;

internal static class Logger
{
    private static readonly object _lock = new();
    private static readonly string LogFile =
        Path.Combine(AppContext.BaseDirectory, "remoteplay.log");

    public static string FilePath => LogFile;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static void Clear()
    {
        try
        {
            lock (_lock)
            {
                File.WriteAllText(LogFile, string.Empty);
            }
        }
        catch
        {
            // Logger must never throw
        }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
            if (ex is not null)
                line += $"{Environment.NewLine}{ex}";

            lock (_lock)
            {
                // Keep log file from growing unbounded — cap at ~5 MB
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    var backup = LogFile + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(LogFile, backup);
                }

                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logger must never throw
        }
    }
}
