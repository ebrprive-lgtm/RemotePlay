using System.IO;
using System.Text.Json;

namespace RemotePlay;

internal sealed class PlaybackHistory
{
    private readonly string _historyFile;
    private readonly object _gate = new();
    private Dictionary<string, PlaybackHistoryEntry> _entries;

    public PlaybackHistory()
        : this(AppPaths.HistoryFile)
    {
    }

    public PlaybackHistory(string historyFile)
    {
        if (string.IsNullOrWhiteSpace(historyFile))
            throw new ArgumentException("History file path is required.", nameof(historyFile));

        _historyFile = historyFile;
        _entries = LoadEntries(historyFile);
    }

    // ── Static convenience wrappers ─────────────────────────────────────────

    public static RecentPlaybackItem[] GetDefaultRecent(int maxItems) =>
        new PlaybackHistory().GetRecent(maxItems);

    public static Dictionary<string, RecentPlaybackItem> GetDefaultResumeMap() =>
        new PlaybackHistory().GetResumeMap();

    public static void ClearDefault(string filePath) =>
        new PlaybackHistory().Clear(filePath);

    // ── Reads ────────────────────────────────────────────────────────────────

    public TimeSpan? GetResumePosition(string filePath, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(filePath) || duration <= TimeSpan.Zero)
            return null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(filePath, out var entry))
                return null;

            var position = TimeSpan.FromSeconds(entry.PositionSeconds);
            return position >= TimeSpan.FromSeconds(10) && duration - position >= TimeSpan.FromSeconds(30)
                ? position
                : null;
        }
    }

    public RecentPlaybackItem[] GetRecent(int maxItems)
    {
        if (maxItems <= 0)
            return [];

        lock (_gate)
        {
            return _entries
                .Where(e => File.Exists(e.Key))
                .OrderByDescending(e => e.Value.UpdatedUtc)
                .Take(maxItems)
                .Select(e => ToRecentItem(e.Key, e.Value))
                .ToArray();
        }
    }

    /// <summary>Returns entries that are resumable (have meaningful progress, not near-complete).</summary>
    public Dictionary<string, RecentPlaybackItem> GetResumeMap() =>
        GetFilteredMap(e => e.PositionSeconds >= 10
                         && e.DurationSeconds > 0
                         && e.DurationSeconds - e.PositionSeconds >= 30);

    /// <summary>Returns all entries with any recorded progress. Used to show the progress bar/badge on cards.</summary>
    public Dictionary<string, RecentPlaybackItem> GetProgressMap() =>
        GetFilteredMap(e => e.PositionSeconds > 0 && e.DurationSeconds > 0);

    /// <summary>Returns paths that have been explicitly marked as watched.</summary>
    public HashSet<string> GetWatchedSet()
    {
        lock (_gate)
        {
            return _entries
                .Where(e => e.Value.Watched)
                .Select(e => e.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public MoviePlaybackPreferences? GetPreferences(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        lock (_gate)
        {
            _entries.TryGetValue(filePath, out var entry);
            return entry?.Preferences;
        }
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    public void SavePosition(string filePath, TimeSpan position, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(filePath) || position < TimeSpan.FromSeconds(10))
            return;

        var nearEnd = duration > TimeSpan.Zero && duration - position < TimeSpan.FromSeconds(30);

        lock (_gate)
        {
            // Near or at end — clamp to duration and mark watched so the card badge persists
            // but no resume prompt appears. Otherwise record the actual position.
            var newPosition = nearEnd ? duration.TotalSeconds : position.TotalSeconds;
            var newWatched  = nearEnd;

            Upsert(filePath, e => e with
            {
                PositionSeconds = Math.Max(0, newPosition),
                DurationSeconds = Math.Max(0, duration.TotalSeconds),
                UpdatedUtc      = DateTimeOffset.UtcNow,
                Watched         = newWatched || (e.Watched)
            });
        }
    }

    public void SavePosition(string filePath, TimeSpan position, TimeSpan duration, int historyLimit)
    {
        SavePosition(filePath, position, duration);
        Trim(historyLimit);
    }

    public void SavePreferences(string filePath, MoviePlaybackPreferences preferences)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        ArgumentNullException.ThrowIfNull(preferences);

        lock (_gate)
        {
            Upsert(filePath, e => e with { Preferences = preferences, UpdatedUtc = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>Sets or clears the explicit watched flag without touching playback position.</summary>
    public void MarkWatched(string filePath, bool watched)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        lock (_gate)
        {
            Upsert(filePath, e => e with { Watched = watched, UpdatedUtc = DateTimeOffset.UtcNow });
        }
    }

    public void Clear(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        lock (_gate)
        {
            if (_entries.Remove(filePath))
                SaveEntries();
        }
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            _entries.Clear();
            SaveEntries();
        }
    }

    public void Trim(int historyLimit)
    {
        var limit = Math.Max(1, historyLimit);

        lock (_gate)
        {
            if (_entries.Count <= limit)
                return;

            var toRemove = _entries
                .OrderByDescending(e => e.Value.UpdatedUtc)
                .Skip(limit)
                .Select(e => e.Key)
                .ToArray();

            foreach (var key in toRemove)
                _entries.Remove(key);

            SaveEntries();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads the existing entry for <paramref name="filePath"/> (or a default blank entry),
    /// applies <paramref name="patch"/>, writes the result back, and flushes to disk.
    /// Must be called inside <c>lock (_gate)</c>.
    /// </summary>
    private void Upsert(string filePath, Func<PlaybackHistoryEntry, PlaybackHistoryEntry> patch)
    {
        _entries.TryGetValue(filePath, out var existing);
        _entries[filePath] = patch(existing ?? new PlaybackHistoryEntry());
        SaveEntries();
    }

    private Dictionary<string, RecentPlaybackItem> GetFilteredMap(Func<PlaybackHistoryEntry, bool> predicate)
    {
        lock (_gate)
        {
            return _entries
                .Where(e => File.Exists(e.Key) && predicate(e.Value))
                .ToDictionary(
                    e => e.Key,
                    e => ToRecentItem(e.Key, e.Value),
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    private static RecentPlaybackItem ToRecentItem(string key, PlaybackHistoryEntry e) =>
        new(key, Math.Max(0, e.PositionSeconds), Math.Max(0, e.DurationSeconds), e.UpdatedUtc);

    private static Dictionary<string, PlaybackHistoryEntry> LoadEntries(string historyFile)
    {
        try
        {
            if (!File.Exists(historyFile))
                return new Dictionary<string, PlaybackHistoryEntry>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(historyFile);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, PlaybackHistoryEntry>>(json);
            return loaded is null
                ? new Dictionary<string, PlaybackHistoryEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, PlaybackHistoryEntry>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load playback history", ex);
            return new Dictionary<string, PlaybackHistoryEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveEntries()
    {
        try
        {
            var directory = Path.GetDirectoryName(_historyFile);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_historyFile,
                JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save playback history", ex);
        }
    }

    // ── Data model ───────────────────────────────────────────────────────────

    private sealed record PlaybackHistoryEntry
    {
        public double PositionSeconds { get; init; }
        public double DurationSeconds { get; init; }
        public DateTimeOffset UpdatedUtc { get; init; }
        public MoviePlaybackPreferences? Preferences { get; init; }
        /// <summary>Explicitly marked as watched by the user (persisted independently of progress).</summary>
        public bool Watched { get; init; }
    }
}

internal sealed record RecentPlaybackItem(string FilePath, double PositionSeconds, double DurationSeconds, DateTimeOffset UpdatedUtc);

internal sealed record MoviePlaybackPreferences
{
    public double Brightness { get; init; } = 0.5;
    public double Saturation { get; init; } = 1;
    public double Zoom { get; init; } = 1;
    public bool ForceSwAudio { get; init; } = false;
}
