using System.IO;

namespace RemotePlay;

/// <summary>Helpers for the <c>.rplink</c> file format — a single-line text file whose content is the
/// absolute path to a video file elsewhere in the library.</summary>
internal static class RplinkHelper
{
    public const string Extension = ".rplink";

    internal enum RplinkTargetStatus
    {
        Resolved,
        Missing,
        UnreadableLink,
        InaccessibleTarget,
    }

    internal readonly record struct RplinkTargetInspection(
        RplinkTargetStatus Status,
        string? ResolvedTarget,
        string? Details);

    /// <summary>Returns true when the file path has the <c>.rplink</c> extension.</summary>
    public static bool IsRplinkFile(string path) =>
        string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the stored text from a <c>.rplink</c> file without resolving it.
    /// Returns <c>null</c> when the file is unreadable or empty.</summary>
    public static string? TryReadStoredTargetText(string rplinkPath)
    {
        try
        {
            var raw = File.ReadAllText(rplinkPath).Trim().TrimStart('\uFEFF');
            raw = raw.Trim('"');
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads the resolved target path from a <c>.rplink</c> file without checking whether
    /// the target exists. Returns <c>null</c> when the file is unreadable or empty.</summary>
    public static string? TryReadTargetRaw(string rplinkPath) =>
        TryResolveTargetPath(rplinkPath, out var targetPath, out _)
            ? targetPath
            : null;

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
        var inspection = InspectTarget(rplinkPath);
        return inspection.Status == RplinkTargetStatus.Resolved
            ? inspection.ResolvedTarget
            : null;
    }

    internal static RplinkTargetInspection InspectTarget(string rplinkPath)
    {
        if (!TryResolveTargetPath(rplinkPath, out var targetPath, out var resolveError))
            return new(RplinkTargetStatus.UnreadableLink, null, resolveError);

        if (File.Exists(targetPath) || Directory.Exists(targetPath))
            return new(RplinkTargetStatus.Resolved, targetPath, null);

        try
        {
            _ = File.GetAttributes(targetPath);
            return new(RplinkTargetStatus.Resolved, targetPath, null);
        }
        catch (FileNotFoundException)
        {
            return new(RplinkTargetStatus.Missing, targetPath, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(RplinkTargetStatus.Missing, targetPath, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(RplinkTargetStatus.InaccessibleTarget, targetPath, BuildErrorDetails(ex));
        }
        catch (IOException ex)
        {
            return new(RplinkTargetStatus.InaccessibleTarget, targetPath, BuildErrorDetails(ex));
        }
        catch (Exception ex)
        {
            return new(RplinkTargetStatus.InaccessibleTarget, targetPath, BuildErrorDetails(ex));
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

    private static bool TryResolveTargetPath(string rplinkPath, out string targetPath, out string? error)
    {
        targetPath = string.Empty;
        error = null;

        try
        {
            var raw = File.ReadAllText(rplinkPath).Trim().TrimStart('\uFEFF');

            // Some links may be stored with accidental wrapping quotes (for example,
            // copied shell strings). Quotes are not valid path characters, so trim
            // them to avoid false "broken link" detections.
            raw = raw.Trim('"');
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "Link file is empty.";
                return false;
            }

            // Handle anchored paths with "@" prefix.
            // Prefer the nearest ancestor that contains @Master.Movies or @Master.Series
            // (library root), then fall back to the drive/share root.
            if (raw.StartsWith("@\\") || raw.StartsWith("@/"))
            {
                var atRoot = ResolveAtPrefixRoot(rplinkPath);
                if (string.IsNullOrEmpty(atRoot))
                {
                    error = "Link root is unavailable for @-prefixed target.";
                    return false;
                }

                // Replace the "@" prefix with the inferred anchor root.
                raw = atRoot.TrimEnd('\\', '/') + raw[1..];
            }

            // Support paths relative to the .rplink file's own directory.
            targetPath = Path.IsPathRooted(raw)
                ? raw
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(rplinkPath) ?? string.Empty, raw));

            return true;
        }
        catch (Exception ex)
        {
            error = BuildErrorDetails(ex);
            return false;
        }
    }

    private static string? ResolveAtPrefixRoot(string rplinkPath)
    {
        var linkDirectory = Path.GetDirectoryName(Path.GetFullPath(rplinkPath));
        var current = linkDirectory;

        while (!string.IsNullOrWhiteSpace(current))
        {
            var hasMasterMovies = Directory.Exists(Path.Combine(current, "@Master.Movies"));
            var hasMasterSeries = Directory.Exists(Path.Combine(current, "@Master.Series"));
            if (hasMasterMovies || hasMasterSeries)
                return current;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return Path.GetPathRoot(Path.GetFullPath(rplinkPath));
    }

    private static string BuildErrorDetails(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message)
            ? ex.GetType().Name
            : $"{ex.GetType().Name}: {ex.Message}";
}
