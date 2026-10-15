using System;
using System.IO;

namespace RemotePlay.VideoPlayer;

internal static class PlayerDiagnostics
{
    private static readonly object SyncRoot = new();
    private static readonly string LogFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemotePlay",
        "Logs",
        "RemotePlay.VideoPlayer.log");

    public static string Path => LogFilePath;

    public static void Info(string message)
        => Write("INFO", message);

    public static void Error(string message, Exception exception)
        => Write("ERROR", $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogFilePath)!);
                File.AppendAllText(LogFilePath, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
