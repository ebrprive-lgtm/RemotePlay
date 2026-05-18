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

    /// <summary>Reads the raw target path from a <c>.rplink</c> file without checking whether
    /// the target exists. Returns <c>null</c> when the file is unreadable or empty.</summary>
    public static string? TryReadTargetRaw(string rplinkPath)
    {
        try
        {
            var raw = File.ReadAllText(rplinkPath).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return Path.IsPathRooted(raw)
                ? raw
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(rplinkPath) ?? string.Empty, raw));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns <c>true</c> when the raw target of the link points to a directory
    /// (folder link), regardless of whether the directory currently exists.</summary>
    public static bool IsTargetFolder(string rplinkPath)
    {
        var raw = TryReadTargetRaw(rplinkPath);
        if (raw is null) return false;
        // Heuristic: if the stored path has no extension it is very likely a folder.
        // We also check on disk when possible.
        if (Directory.Exists(raw)) return true;
        if (File.Exists(raw)) return false;
        return string.IsNullOrEmpty(Path.GetExtension(raw));
    }

    /// <summary>Reads the target path from a <c>.rplink</c> file.
    /// Returns <c>null</c> when the link is unreadable, empty, or the target does not exist
    /// (either as a file or a directory).</summary>
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

            return (File.Exists(target) || Directory.Exists(target)) ? target : null;
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

    /// <summary>Returns a path relative to <paramref name="linkPath"/>'s directory when both
    /// paths share the same volume root, otherwise returns <paramref name="targetPath"/> unchanged.
    /// Relative links make the library portable across drive-letter changes.</summary>
    public static string MakeRelativeIfPossible(string linkPath, string targetPath)
    {
        try
        {
            var linkRoot   = Path.GetPathRoot(Path.GetFullPath(linkPath));
            var targetRoot = Path.GetPathRoot(Path.GetFullPath(targetPath));

            if (string.IsNullOrEmpty(linkRoot) || string.IsNullOrEmpty(targetRoot))
                return targetPath;

            if (!string.Equals(linkRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
                return targetPath;   // different volumes — must keep absolute

            var linkDir = Path.GetDirectoryName(Path.GetFullPath(linkPath)) ?? string.Empty;
            return Path.GetRelativePath(linkDir, Path.GetFullPath(targetPath));
        }
        catch
        {
            return targetPath;
        }
    }
}
