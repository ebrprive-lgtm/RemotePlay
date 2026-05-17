using System.IO;

namespace RemotePlay;

/// <summary>Helpers for the <c>.rplink</c> file format — a single-line text file whose content is the
/// absolute path to a video file elsewhere in the library.</summary>
internal static class RplinkHelper
{
    public const string Extension = ".rplink";

    /// <summary>Returns true when the file path has the <c>.rplink</c> extension.</summary>
    public static bool IsRplinkFile(string path) =>
        string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the target path from a <c>.rplink</c> file.
    /// Returns <c>null</c> when the link is unreadable, empty, or the target file no longer exists.</summary>
    public static string? TryReadTarget(string rplinkPath)
    {
        try
        {
            var raw = File.ReadAllText(rplinkPath).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // Support paths relative to the .rplink file's own directory.
            var target = Path.IsPathRooted(raw)
                ? raw
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(rplinkPath) ?? string.Empty, raw));

            return File.Exists(target) ? target : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Creates (or overwrites) a <c>.rplink</c> file at <paramref name="linkPath"/>
    /// that points to <paramref name="targetPath"/>.</summary>
    public static void Create(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        File.WriteAllText(linkPath, targetPath);
    }
}
