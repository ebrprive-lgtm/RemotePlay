using System.IO;

namespace RemotePlay;

internal sealed record MovieFile(string Name, string FullPath);

internal static class MovieScanner
{
    private static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv"];

    public static IReadOnlyList<MovieFile> Scan(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                Logger.Info($"Movies directory not found, creating: {directory}");
                Directory.CreateDirectory(directory);
                return [];
            }

            return Directory
                .EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(
                    Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .Select(f => new MovieFile(
                    Name: Path.GetFileNameWithoutExtension(f),
                    FullPath: f))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to scan movies directory", ex);
            return [];
        }
    }
}
