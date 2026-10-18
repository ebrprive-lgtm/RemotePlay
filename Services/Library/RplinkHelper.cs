using System.Globalization;
using System.IO;

namespace RemotePlay;

/// <summary>Helpers for the <c>.rplink</c> file format — a single-line text file whose content is a
/// target path or a library directive such as <c>@Series</c> or <c>@Movie</c>. Empty links are
/// inferred from a same-name series folder or movie file. Year-prefixed names also search their
/// corresponding decade folders, including legacy concrete paths to the old flat master layout.</summary>
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

    /// <summary>Reads the resolved target path from a <c>.rplink</c> file without requiring the
    /// target to exist. A missing <c>@Movie</c> target is returned as its expected wildcard path.</summary>
    public static string? TryReadTargetRaw(string rplinkPath) =>
        TryResolveTargetPath(rplinkPath, out var targetPath, out _, out _, out _)
            ? targetPath
            : null;

    internal static string GetMasterLibraryTargetDirectory(string masterLibraryDirectory, string itemName)
    {
        var decade = GetDecadeFolderName(itemName);
        return decade is null ? masterLibraryDirectory : Path.Combine(masterLibraryDirectory, decade);
    }

    /// <summary>Returns <c>true</c> when the raw target of the link points to a directory
    /// (folder link), regardless of whether the directory currently exists.</summary>
    public static bool IsTargetFolder(string rplinkPath)
    {
        if (!TryResolveTargetPath(
            rplinkPath,
            out var raw,
            out _,
            out var isSeriesTarget,
            out var missingMovieTarget))
            return false;

        if (isSeriesTarget) return true;
        if (missingMovieTarget) return false;

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
        if (!TryResolveTargetPath(
            rplinkPath,
            out var targetPath,
            out var resolveError,
            out _,
            out var missingMovieTarget))
            return new(RplinkTargetStatus.UnreadableLink, null, resolveError);

        if (missingMovieTarget)
        {
            return resolveError is null
                ? new(RplinkTargetStatus.Missing, targetPath, null)
                : new(RplinkTargetStatus.InaccessibleTarget, targetPath, resolveError);
        }

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

    private static bool TryResolveTargetPath(
        string rplinkPath,
        out string targetPath,
        out string? error,
        out bool isSeriesTarget,
        out bool missingMovieTarget)
    {
        targetPath = string.Empty;
        error = null;
        isSeriesTarget = false;
        missingMovieTarget = false;

        try
        {
            var raw = File.ReadAllText(rplinkPath).Trim().TrimStart('\uFEFF');

            // Some links may be stored with accidental wrapping quotes (for example,
            // copied shell strings). Quotes are not valid path characters, so trim
            // them to avoid false "broken link" detections.
            raw = raw.Trim('"');
            if (string.IsNullOrWhiteSpace(raw))
            {
                var libraryRoot = ResolveAtPrefixRoot(rplinkPath);
                if (string.IsNullOrEmpty(libraryRoot))
                {
                    error = "Link root is unavailable for empty-link target inference.";
                    return false;
                }

                var linkName = Path.GetFileNameWithoutExtension(rplinkPath);
                var seriesResolution = ResolveSeriesTarget(libraryRoot, linkName);
                if (seriesResolution.Exists)
                {
                    targetPath = seriesResolution.TargetPath;
                    isSeriesTarget = true;
                    return true;
                }

                return TryResolveMovieTarget(
                    libraryRoot,
                    linkName,
                    out targetPath,
                    out error,
                    out missingMovieTarget);
            }

            if (string.Equals(raw, "@Series", StringComparison.OrdinalIgnoreCase))
            {
                var libraryRoot = ResolveAtPrefixRoot(rplinkPath);
                if (string.IsNullOrEmpty(libraryRoot))
                {
                    error = "Link root is unavailable for the @Series target.";
                    return false;
                }

                var seriesResolution = ResolveSeriesTarget(
                    libraryRoot,
                    Path.GetFileNameWithoutExtension(rplinkPath));
                isSeriesTarget = true;
                targetPath = seriesResolution.TargetPath;
                return true;
            }

            if (string.Equals(raw, "@Movie", StringComparison.OrdinalIgnoreCase))
            {
                var libraryRoot = ResolveAtPrefixRoot(rplinkPath);
                if (string.IsNullOrEmpty(libraryRoot))
                {
                    error = "Link root is unavailable for the @Movie target.";
                    return false;
                }

                return TryResolveMovieTarget(
                    libraryRoot,
                    Path.GetFileNameWithoutExtension(rplinkPath),
                    out targetPath,
                    out error,
                    out missingMovieTarget);
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

            // Older concrete links can still point at the former flat master-folder layout.
            if (TryResolveDecadeTargetPath(targetPath, out var decadeTarget, out var decadeSeriesTarget))
            {
                targetPath = decadeTarget;
                isSeriesTarget = decadeSeriesTarget;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = BuildErrorDetails(ex);
            return false;
        }
    }

    internal static bool TryResolveDecadeTargetPath(
        string targetPath,
        out string resolvedTarget,
        out bool isSeriesTarget)
    {
        resolvedTarget = targetPath;
        isSeriesTarget = false;

        if (File.Exists(targetPath) || Directory.Exists(targetPath))
            return false;

        var normalizedPath = Path.TrimEndingDirectorySeparator(targetPath);
        var masterDirectory = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(masterDirectory))
            return false;

        var masterDirectoryName = Path.GetFileName(masterDirectory);
        var targetName = Path.GetFileName(normalizedPath);
        var isMovieTarget = string.Equals(
            masterDirectoryName,
            "@Master.Movies",
            StringComparison.OrdinalIgnoreCase);
        var isSeriesCandidate = string.Equals(
            masterDirectoryName,
            "@Master.Series",
            StringComparison.OrdinalIgnoreCase);

        if (!isMovieTarget && !isSeriesCandidate)
            return false;

        if (isMovieTarget && !MovieScanner.IsSupportedVideoFile(normalizedPath))
            return false;

        var itemName = isMovieTarget
            ? Path.GetFileNameWithoutExtension(targetName)
            : targetName;
        var decade = GetDecadeFolderName(itemName);
        if (decade is null)
            return false;

        var decadeTarget = Path.Combine(masterDirectory, decade, targetName);
        var targetExists = isMovieTarget
            ? File.Exists(decadeTarget)
            : Directory.Exists(decadeTarget);
        if (!targetExists)
            return false;

        resolvedTarget = decadeTarget;
        isSeriesTarget = isSeriesCandidate;
        return true;
    }

    private static bool TryResolveMovieTarget(
        string libraryRoot,
        string movieName,
        out string targetPath,
        out string? error,
        out bool missingMovieTarget)
    {
        var movieDirectory = Path.Combine(libraryRoot, "@Master.Movies");
        var movieDirectories = new List<string> { movieDirectory };
        var decade = GetDecadeFolderName(movieName);
        if (decade is not null)
            movieDirectories.Add(Path.Combine(movieDirectory, decade));

        var expectedPattern = Path.Combine(movieDirectories[^1], movieName + ".*");
        targetPath = expectedPattern;
        error = null;
        missingMovieTarget = true;

        foreach (var candidateDirectory in movieDirectories)
        {
            targetPath = Path.Combine(candidateDirectory, movieName + ".*");
            try
            {
                var movieTarget = Directory
                    .EnumerateFiles(
                        candidateDirectory,
                        movieName + ".*",
                        SearchOption.TopDirectoryOnly)
                    .Where(MovieScanner.IsSupportedVideoFile)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (movieTarget is null)
                    continue;

                targetPath = movieTarget;
                missingMovieTarget = false;
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = BuildErrorDetails(ex);
                return true;
            }
            catch (IOException ex)
            {
                error = BuildErrorDetails(ex);
                return true;
            }
        }

        return true;
    }

    private static (string TargetPath, bool Exists) ResolveSeriesTarget(
        string libraryRoot,
        string seriesName)
    {
        var seriesDirectory = Path.Combine(libraryRoot, "@Master.Series");
        var directTarget = Path.Combine(seriesDirectory, seriesName);
        if (Directory.Exists(directTarget))
            return (directTarget, true);

        var decade = GetDecadeFolderName(seriesName);
        if (decade is null)
            return (directTarget, false);

        var decadeTarget = Path.Combine(seriesDirectory, decade, seriesName);
        return (decadeTarget, Directory.Exists(decadeTarget));
    }

    private static string? GetDecadeFolderName(string name)
    {
        if (name.Length < 4 ||
            name[0] is < '0' or > '9' ||
            name[1] is < '0' or > '9' ||
            name[2] is < '0' or > '9' ||
            name[3] is < '0' or > '9' ||
            (name.Length > 4 && !char.IsWhiteSpace(name[4]) && name[4] != '-'))
        {
            return null;
        }

        var year =
            (name[0] - '0') * 1000 +
            (name[1] - '0') * 100 +
            (name[2] - '0') * 10 +
            (name[3] - '0');
        return (year / 10 * 10).ToString(CultureInfo.InvariantCulture) + "s";
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
