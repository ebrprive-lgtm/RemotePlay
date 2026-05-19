using System.IO;
using System.Linq;

namespace RemotePlay;

/// <summary>Static helpers for recursive folder inspection used by the browser folder operations.</summary>
internal static class FolderOperationsHelper
{
    /// <summary>
    /// Recursively counts all files and <c>.rplink</c> link files under <paramref name="folderPath"/>.
    /// </summary>
    /// <param name="folderPath">Root folder to examine.</param>
    /// <returns>Total file count and link file count.</returns>
    internal static (int TotalFiles, int LinkFiles) CountFolderContents(string folderPath)
    {
        var files     = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).ToList();
        int linkCount = files.Count(f => RplinkHelper.IsRplinkFile(f));
        return (files.Count, linkCount);
    }

    /// <summary>
    /// Returns all <c>.rplink</c> files under <paramref name="libraryRoot"/> whose stored target
    /// is inside (or equal to) <paramref name="sourceDir"/>.
    /// </summary>
    internal static IReadOnlyList<string> FindLinksPointingIntoFolder(string libraryRoot, string sourceDir)
    {
        return Directory
            .EnumerateFiles(libraryRoot, "*" + RplinkHelper.Extension, SearchOption.AllDirectories)
            .Where(link =>
            {
                var raw = RplinkHelper.TryReadTargetRaw(link);
                if (raw is null) return false;
                return string.Equals(raw, sourceDir, StringComparison.OrdinalIgnoreCase)
                    || raw.StartsWith(sourceDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }
}
