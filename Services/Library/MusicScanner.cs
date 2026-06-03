using System.IO;
using System.Threading;

namespace RemotePlay;

/// <summary>Represents a single indexed music file with lightweight metadata for fast in-memory filtering.</summary>
internal sealed record MusicFile(
    string Name,
    string FullPath,
    string? Genre = null,
    uint Year = 0,
    int Duration = -1);   // seconds; -1 = not yet enriched

/// <summary>Represents an indexed M3U/M3U8 playlist: resolved absolute track paths and an album hint derived from the filename.</summary>
internal sealed record M3uEntry(
    string M3uPath,
    string AlbumHint,
    string[] TrackPaths);

/// <summary>
/// A live scan job whose directory queue can be reprioritised at any time.
/// When the user navigates to a folder that is still pending, that folder is
/// moved to the front so it is scanned immediately, after which normal BFS
/// order resumes.
/// </summary>
internal sealed class MusicScanJob
{
    private readonly LinkedList<string> _queue = new();
    // Directories already scanned (or force-injected and being scanned) so BFS
    // can skip them when it naturally enqueues the same path later.
    private readonly HashSet<string> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    internal MusicScanJob(string rootDirectory)
    {
        _queue.AddLast(rootDirectory);
    }

    /// <summary>
    /// Returns the next pending directory, or null when the queue is empty.
    /// Marks the returned directory as scanned so duplicates are skipped.
    /// </summary>
    internal string? Dequeue()
    {
        lock (_lock)
        {
            while (_queue.Count > 0)
            {
                var dir = _queue.First!.Value;
                _queue.RemoveFirst();
                // Skip if already scanned (can happen after a Prioritize injection)
                if (_scanned.Add(dir))
                    return dir;
            }
            return null;
        }
    }

    /// <summary>Enqueues <paramref name="directory"/> at the back (normal BFS order), unless already scanned.</summary>
    internal void Enqueue(string directory)
    {
        lock (_lock)
        {
            if (!_scanned.Contains(directory))
                _queue.AddLast(directory);
        }
    }

    /// <summary>
    /// Promotes <paramref name="directory"/> (and any of its subdirectories already
    /// in the queue) to the front so they are processed immediately.
    /// <para>
    /// If the directory is not yet in the queue (BFS hasn't reached its parent yet),
    /// it is injected at the front anyway so the user sees results without waiting
    /// for the BFS wave to arrive naturally.
    /// </para>
    /// </summary>
    internal void Prioritize(string directory)
    {
        lock (_lock)
        {
            // Already done — nothing to promote.
            if (_scanned.Contains(directory))
                return;

            var toFront = new List<string>();
            var node = _queue.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                {
                    toFront.Add(node.Value);
                    _queue.Remove(node);
                }
                node = next;
            }

            // If the directory was not in the queue at all (BFS hasn't reached the
            // parent yet), inject it directly so it is scanned right now.
            if (toFront.Count == 0)
                toFront.Add(directory);

            // Re-insert at the front in original relative order.
            for (int i = toFront.Count - 1; i >= 0; i--)
                _queue.AddFirst(toFront[i]);
        }
    }

    internal bool HasWork
    {
        get { lock (_lock) return _queue.Count > 0; }
    }

    /// <summary>
    /// Marks <paramref name="directory"/> as already scanned so the background BFS
    /// skips it when it naturally dequeues the path.
    /// </summary>
    internal void MarkScanned(string directory)
    {
        lock (_lock)
        {
            _scanned.Add(directory);
            // Remove from the queue if it is still pending.
            var node = _queue.First;
            while (node is not null)
            {
                var next = node.Next;
                if (string.Equals(node.Value, directory, StringComparison.OrdinalIgnoreCase))
                    _queue.Remove(node);
                node = next;
            }
        }
    }
}

internal static class MusicScanner
{
    /// <summary>
    /// Scans <paramref name="job"/>'s directory queue for audio files using BFS.
    /// <para>
    /// After each directory is fully scanned, discovered files are delivered via
    /// <paramref name="onFolderComplete"/> so the caller can update the live index
    /// incrementally without waiting for the full scan to finish.
    /// </para>
    /// <para>
    /// Call <see cref="MusicScanJob.Prioritize"/> from any thread at any time to
    /// promote a folder to the front of the queue.
    /// </para>
    /// </summary>
    public static void Scan(
        MusicScanJob job,
        IEnumerable<string> extensions,
        Action<IReadOnlyList<MusicFile>>? onFolderComplete = null,
        Action<string>? onFolder = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? ignoredFolderNames = null)
    {
        var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        int total = 0;

        while (job.HasWork)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dir = job.Dequeue();
            if (dir is null) break;

            onFolder?.Invoke(dir);

            // Scan files in this directory
            var folderFiles = new List<MusicFile>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!extSet.Contains(Path.GetExtension(f))) continue;
                    folderFiles.Add(new MusicFile(Name: Path.GetFileNameWithoutExtension(f), FullPath: f));
                    total++;
                    progress?.Report(total);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Warning("MusicScanner", $"Skipping inaccessible folder '{dir}': {ex.Message}");
            }

            if (folderFiles.Count > 0)
                onFolderComplete?.Invoke(folderFiles);

            // Enqueue subdirectories at the back (BFS)
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    if (ignoredFolderNames?.Contains(Path.GetFileName(sub)) == true) continue;
                    job.Enqueue(sub);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("MusicScanner", $"Cannot list subdirectories of '{dir}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Second-pass enrichment: reads genre and year from audio tags for every entry in
    /// <paramref name="index"/> that has not yet been enriched, calling
    /// <paramref name="onBatchEnriched"/> periodically so the caller can swap in the
    /// updated snapshot. Designed to run on a background thread after the fast path scan.
    /// </summary>
    public static void EnrichWithTags(
        MusicFile[] index,
        Action<MusicFile[]> onBatchEnriched,
        int batchSize = 200,
        CancellationToken cancellationToken = default)
    {
        // Work on a mutable copy so we can update fields without touching the live index.
        var enriched = (MusicFile[])index.Clone();
        int changed = 0;

        for (int i = 0; i < enriched.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mf = enriched[i];
            // Skip files that were fully enriched (genre/year present AND duration already read).
            if ((mf.Genre != null || mf.Year > 0) && mf.Duration >= 0) continue;

            try
            {
                using var tagFile = TagLib.File.Create(mf.FullPath);
                var genre    = mf.Genre ?? tagFile.Tag.FirstGenre;
                var year     = mf.Year > 0 ? mf.Year : tagFile.Tag.Year;
                var duration = mf.Duration >= 0 ? mf.Duration : (int)Math.Round(tagFile.Properties.Duration.TotalSeconds);
                if (genre != mf.Genre || year != mf.Year || duration != mf.Duration)
                {
                    enriched[i] = mf with { Genre = genre, Year = year, Duration = duration };
                    changed++;
                }
            }
            catch { /* unreadable tags — leave as-is */ }

            if (changed > 0 && (i + 1) % batchSize == 0)
            {
                onBatchEnriched((MusicFile[])enriched.Clone());
                changed = 0;
            }
        }

        // Final flush
        if (changed > 0)
            onBatchEnriched((MusicFile[])enriched.Clone());
    }

    /// <summary>
    /// Scans <paramref name="rootDirectory"/> recursively for M3U/M3U8 files and resolves
    /// each playlist's track paths to absolute paths that exist on disk.
    /// Returns a dictionary keyed by the absolute M3U file path.
    /// This runs once during indexing so dynamic-folder expand never needs to touch the filesystem.
    /// </summary>
    public static Dictionary<string, M3uEntry> ScanM3uFiles(
        string rootDirectory,
        IReadOnlySet<string>? audioExtensions = null,
        IReadOnlySet<string>? ignoredFolderNames = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, M3uEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(rootDirectory)) return result;

        var queue = new Queue<string>();
        queue.Enqueue(rootDirectory);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();

            // Enumerate M3U/M3U8 files in this directory
            try
            {
                foreach (var m3uPath in Directory.EnumerateFiles(dir, "*.m3u*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(m3uPath);
                    if (!string.Equals(ext, ".m3u",  StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ext, ".m3u8", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var m3uDir    = Path.GetDirectoryName(m3uPath) ?? string.Empty;
                    var albumHint = AlbumFromM3uStem(Path.GetFileNameWithoutExtension(m3uPath));
                    var tracks    = new List<string>();

                    try
                    {
                        foreach (var raw in File.ReadLines(m3uPath))
                        {
                            var line = raw.Trim();
                            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
                            var normalised = line.Replace('/', Path.DirectorySeparatorChar);
                            var resolved   = Path.IsPathRooted(normalised)
                                ? normalised
                                : Path.GetFullPath(Path.Combine(m3uDir, normalised));
                            // Filter by audio extension only — no File.Exists() to avoid slow network I/O
                            // on UNC shares (164k existence checks over \\server\share takes minutes).
                            if (audioExtensions != null &&
                                !audioExtensions.Contains(Path.GetExtension(resolved), StringComparer.OrdinalIgnoreCase))
                                continue;
                            tracks.Add(resolved);
                        }
                    }
                    catch { /* unreadable playlist — skip */ }

                    if (tracks.Count > 0)
                        result[m3uPath] = new M3uEntry(m3uPath, albumHint, tracks.ToArray());
                }
            }
            catch { /* inaccessible directory — skip */ }

            // BFS into subdirectories
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    if (ignoredFolderNames?.Contains(Path.GetFileName(sub)) == true) continue;
                    queue.Enqueue(sub);
                }
            }
            catch { /* ignore */ }
        }

        return result;
    }

    // Parses "YYYY_AlbumName" or "AlbumName" and returns the album part.
    private static string AlbumFromM3uStem(string stem)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stem, @"^\d{4}[_\-]+(.+)$");
        var album = m.Success ? m.Groups[1].Value : stem;
        return album.Replace('_', ' ').Trim();
    }
}
