using System.IO;
using System.Linq;
using RemotePlay.Services;

namespace RemotePlay;

internal sealed partial class WebServer
{
    /// <summary>
    /// Returns the number of indexed link entries whose resolved target is inside
    /// (or equal to) <paramref name="folderPath"/>. Uses the in-memory index — O(n) with no disk I/O.
    /// </summary>
    public int CountIndexedLinksPointingIntoFolder(string folderPath)
    {
        var index = _libraryIndex; // snapshot — array reference is replaced atomically on rescan
        var prefix = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return index.Count(f =>
            f.IsLink &&
            (string.Equals(f.FilePath, folderPath, StringComparison.OrdinalIgnoreCase) ||
             f.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Returns the .rplink file paths from the index whose resolved target equals
    /// <paramref name="targetFilePath"/>. Uses the in-memory index — no disk I/O.
    /// Returns <c>null</c> when the index is empty or not yet built.
    /// </summary>
    public string[]? GetIndexedLinkSourcesForFile(string targetFilePath)
    {
        var index = _libraryIndex;
        if (index.Length == 0) return null;

        return index
            .Where(f => f.IsLink &&
                        f.LinkSourcePath is not null &&
                        string.Equals(f.FilePath, targetFilePath, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.LinkSourcePath!)
            .ToArray();
    }

    /// <summary>
    /// Returns a set of all paths tracked by the current index: for regular files this is
    /// the file path; for links this is the .rplink file path. Used by the Check Index command.
    /// Returns an empty set when the index has not been built yet.
    /// </summary>
    public HashSet<string> GetIndexedPathSet()
    {
        var index = _libraryIndex;
        var set = new HashSet<string>(index.Length * 2, StringComparer.OrdinalIgnoreCase);
        foreach (var f in index)
        {
            // Always add FilePath (the video file path) so browser movie-rows can be matched
            // regardless of whether the index entry is a direct file or a deduplicated link entry.
            set.Add(Path.GetFullPath(f.FilePath));

            // Also add the .rplink file path so browser link-rows can be matched.
            if (f.IsLink && f.LinkSourcePath is not null)
                set.Add(Path.GetFullPath(f.LinkSourcePath));
        }
        return set;
    }

    /// <summary>Returns the set of folder names that the library scanner ignores (e.g. "Subs", "Alt").
    /// Files inside these folders are intentionally excluded from the index.</summary>
    public IReadOnlySet<string> GetIgnoredFolderNames() => _hiddenFolderNames;

    /// <summary>
    /// Prevents the <see cref="FileSystemWatcher"/> from scheduling a rescan for
    /// <paramref name="duration"/> by advancing the watcher-started timestamp.
    /// Call this before making file-system changes that you want to handle via targeted index updates.
    /// </summary>
    public void SuppressWatcher(TimeSpan duration)
    {
        // By moving _libraryWatcherStartedUtc into the future, any watcher event that fires
        // while we are making changes will be dropped by the warm-up guard in ScheduleLibraryRescan.
        _libraryWatcherStartedUtc = DateTimeOffset.UtcNow.Add(duration);
    }

    /// <summary>Removes all index entries whose <see cref="LibraryFile.FilePath"/> or
    /// <see cref="LibraryFile.LinkSourcePath"/> starts with <paramref name="prefix"/> (folder delete/move).</summary>
    public void IndexRemoveUnderPath(string prefix)
    {
        var normalPrefix = Path.GetFullPath(prefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
        // Also match an exact path (single file / link)
        var exact = Path.GetFullPath(prefix);

        lock (_libraryIndexGate)
        {
            _libraryIndex = _libraryIndex.Where(f =>
            {
                var fp  = Path.GetFullPath(f.FilePath);
                var lsp = f.LinkSourcePath is not null ? Path.GetFullPath(f.LinkSourcePath) : null;
                bool matchFile = string.Equals(fp, exact, StringComparison.OrdinalIgnoreCase)
                                 || fp.StartsWith(normalPrefix, StringComparison.OrdinalIgnoreCase);
                bool matchLink = lsp is not null && (
                                 string.Equals(lsp, exact, StringComparison.OrdinalIgnoreCase)
                                 || lsp.StartsWith(normalPrefix, StringComparison.OrdinalIgnoreCase));
                return !matchFile && !matchLink;
            }).ToArray();
        }
        Logger.Info($"IndexRemoveUnderPath: removed entries under '{prefix}'; index now has {_libraryIndex.Length} entries");
        SaveLibraryIndexCache();
    }

    /// <summary>Adds or replaces the index entry for a single video file or .rplink file.</summary>
    public void IndexAddOrUpdateFile(string filePath)
    {
        var root = _config.ResolvedMoviesPath;
        LibraryFile? entry = RplinkHelper.IsRplinkFile(filePath)
            ? LibraryIndexHelpers.BuildLibraryFileForLink(root, filePath)
            : WebPathHelpers.IsVideoFile(filePath, _videoExtensions)
                ? LibraryIndexHelpers.BuildLibraryFile(root, filePath)
                : null;

        if (entry is null) return;

        lock (_libraryIndexGate)
        {
            // Remove any existing entry for the same FilePath/LinkSourcePath, then add the new one.
            var without = _libraryIndex.Where(f =>
                !string.Equals(f.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase) &&
                !(f.LinkSourcePath is not null && entry.LinkSourcePath is not null &&
                  string.Equals(f.LinkSourcePath, entry.LinkSourcePath, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            _libraryIndex = [.. without, entry];
        }
        Logger.Info($"IndexAddOrUpdateFile: upserted '{filePath}'; index now has {_libraryIndex.Length} entries");
        SaveLibraryIndexCache();
    }

    /// <summary>Updates every entry whose <see cref="LibraryFile.FilePath"/> or
    /// <see cref="LibraryFile.LinkSourcePath"/> begins with <paramref name="oldPrefix"/>
    /// by replacing that prefix with <paramref name="newPrefix"/> (folder rename).</summary>
    public void IndexRenamePrefix(string oldPrefix, string newPrefix)
    {
        var normOld = Path.GetFullPath(oldPrefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;
        var normNew = Path.GetFullPath(newPrefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;

        static string Reprefix(string path, string oldP, string newP) =>
            newP + path.Substring(oldP.Length);

        lock (_libraryIndexGate)
        {
            _libraryIndex = _libraryIndex.Select(f =>
            {
                var fp  = Path.GetFullPath(f.FilePath);
                var lsp = f.LinkSourcePath is not null ? Path.GetFullPath(f.LinkSourcePath) : null;

                bool fpMatch  = fp.StartsWith(normOld, StringComparison.OrdinalIgnoreCase);
                bool lspMatch = lsp is not null && lsp.StartsWith(normOld, StringComparison.OrdinalIgnoreCase);

                if (!fpMatch && !lspMatch) return f;

                var newFp  = fpMatch  ? Reprefix(fp,  normOld, normNew) : fp;
                var newLsp = lspMatch ? Reprefix(lsp!, normOld, normNew) : f.LinkSourcePath;

                return f with { FilePath = newFp, LinkSourcePath = newLsp };
            }).ToArray();
        }
        Logger.Info($"IndexRenamePrefix: '{oldPrefix}' -> '{newPrefix}'; index now has {_libraryIndex.Length} entries");
        SaveLibraryIndexCache();
    }

    /// <summary>Renames a single file entry in the index (file rename, not folder).</summary>
    public void IndexRenameFile(string oldPath, string newPath)
    {
        lock (_libraryIndexGate)
        {
            _libraryIndex = _libraryIndex.Select(f =>
            {
                bool fpMatch  = string.Equals(Path.GetFullPath(f.FilePath),      Path.GetFullPath(oldPath), StringComparison.OrdinalIgnoreCase);
                bool lspMatch = f.LinkSourcePath is not null &&
                                string.Equals(Path.GetFullPath(f.LinkSourcePath), Path.GetFullPath(oldPath), StringComparison.OrdinalIgnoreCase);

                if (fpMatch)  return f with { FilePath = newPath };
                if (lspMatch) return f with { LinkSourcePath = newPath };
                return f;
            }).ToArray();
        }
        Logger.Info($"IndexRenameFile: '{oldPath}' -> '{newPath}'");
        SaveLibraryIndexCache();
    }
}
