using System.IO;
using System.Threading;

namespace RemotePlay;

internal sealed record MusicFile(string Name, string FullPath);

internal static class MusicScanner
{
    /// <summary>
    /// Scans <paramref name="directory"/> for audio files, walking the tree folder-by-folder so
    /// that a single slow or inaccessible subfolder cannot stall the entire scan.
    /// Progress is reported every 100 tracks found.
    /// </summary>
    public static IReadOnlyList<MusicFile> Scan(
        string directory,
        IEnumerable<string> extensions,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(directory))
            {
                Logger.Info($"Music directory not found, creating: {directory}");
                Directory.CreateDirectory(directory);
                return [];
            }

            var results = new List<MusicFile>();
            ScanDirectory(directory, extSet, results, progress, cancellationToken);
            results.Sort((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
            return results;
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Music scan cancelled");
            return [];
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to scan music directory", ex);
            return [];
        }
    }

    private static void ScanDirectory(
        string directory,
        HashSet<string> extSet,
        List<MusicFile> results,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Enumerate files in this directory — if this folder is inaccessible, log and skip it
        try
        {
            foreach (var f in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (!extSet.Contains(Path.GetExtension(f))) continue;
                results.Add(new MusicFile(Name: Path.GetFileNameWithoutExtension(f), FullPath: f));
                if (results.Count % 100 == 0)
                    progress?.Report(results.Count);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warning("MusicScanner", $"Skipping inaccessible folder '{directory}': {ex.Message}");
        }

        // Recurse into subdirectories individually so one bad folder doesn't abort everything
        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            Logger.Warning("MusicScanner", $"Cannot list subdirectories of '{directory}': {ex.Message}");
            return;
        }

        foreach (var sub in subdirs)
        {
            ct.ThrowIfCancellationRequested();
            ScanDirectory(sub, extSet, results, progress, ct);
        }
    }
}
