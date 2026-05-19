using System.IO;

namespace RemotePlay;

/// <summary>Pure static helpers for building the file-system library index entries and navigation breadcrumbs.</summary>
internal static class LibraryIndexHelpers
{
    /// <summary>Builds the searchable text for a file by turning its path relative to the library root into space-separated tokens.</summary>
    public static string BuildSearchText(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        return relative.Replace(Path.DirectorySeparatorChar, ' ')
            .Replace(Path.AltDirectorySeparatorChar, ' ');
    }

    /// <summary>Builds a <see cref="LibraryFile"/> index entry for a regular video file.</summary>
    public static LibraryFile BuildLibraryFile(string root, string filePath)
    {
        long sizeBytes = 0;
        DateTime lastWriteUtc = default;
        try
        {
            var info = new FileInfo(filePath);
            sizeBytes = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch { }

        return new LibraryFile(
            Path.GetFileNameWithoutExtension(filePath),
            filePath,
            WebPathHelpers.EncodePath(filePath),
            Path.GetFileName(Path.GetDirectoryName(filePath)) ?? string.Empty,
            BuildSearchText(root, filePath),
            sizeBytes,
            lastWriteUtc);
    }

    /// <summary>
    /// Builds a <see cref="LibraryFile"/> for a <c>.rplink</c> file by resolving its target.
    /// Returns <c>null</c> when the target cannot be resolved or when the target is a directory
    /// (folder links are browseable but not indexed as video files).
    /// </summary>
    public static LibraryFile? BuildLibraryFileForLink(string root, string rplinkPath)
    {
        var targetPath = RplinkHelper.TryReadTarget(rplinkPath);
        if (targetPath is null)
            return null;

        if (Directory.Exists(targetPath))
            return null;

        long sizeBytes = 0;
        DateTime lastWriteUtc = default;
        try
        {
            var info = new FileInfo(targetPath);
            sizeBytes = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch { }

        return new LibraryFile(
            Path.GetFileNameWithoutExtension(rplinkPath),
            targetPath,
            WebPathHelpers.EncodePath(targetPath),
            Path.GetFileName(Path.GetDirectoryName(rplinkPath)) ?? string.Empty,
            BuildSearchText(root, rplinkPath),
            sizeBytes,
            lastWriteUtc,
            IsLink: true,
            LinkSourcePath: rplinkPath);
    }

    /// <summary>
    /// Recursively enumerates all files under <paramref name="root"/>, skipping
    /// any directory whose name appears in <paramref name="ignoredFolderNames"/>.
    /// </summary>
    public static IEnumerable<string> EnumerateLibraryVideoFiles(
        string root,
        IReadOnlySet<string> ignoredFolderNames,
        Action? onFolderScanned = null)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            onFolderScanned?.Invoke();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
                yield return file;

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var subdir in subdirs)
            {
                if (!ignoredFolderNames.Contains(Path.GetFileName(subdir)))
                    pending.Push(subdir);
            }
        }
    }

    /// <summary>Builds an ordered breadcrumb list from the library <paramref name="root"/> down to <paramref name="targetDir"/>.</summary>
    public static object[] BuildBreadcrumbs(string root, string targetDir)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var crumbs = new List<object>
        {
            new { name = Path.GetFileName(normalizedRoot), dir = WebPathHelpers.EncodePath(normalizedRoot) }
        };

        var relative = Path.GetRelativePath(normalizedRoot, normalizedTarget);
        if (relative == ".")
            return crumbs.ToArray();

        var current = normalizedRoot;
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            crumbs.Add(new { name = part, dir = WebPathHelpers.EncodePath(current) });
        }

        return crumbs.ToArray();
    }
}
