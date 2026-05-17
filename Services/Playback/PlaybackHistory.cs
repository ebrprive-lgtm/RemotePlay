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

    public static RecentPlaybackItem[] GetDefaultRecent(int maxItems) =>
        new PlaybackHistory().GetRecent(maxItems);

    public static Dictionary<string, RecentPlaybackItem> GetDefaultResumeMap() =>
        new PlaybackHistory().GetResumeMap();

    public static void ClearDefault(string filePath) =>
        new PlaybackHistory().Clear(filePath);

    public PlaybackHistory(string historyFile)
    {
        if (string.IsNullOrWhiteSpace(historyFile))
            throw new ArgumentException("History file path is required.", nameof(historyFile));

        _historyFile = historyFile;
        _entries = LoadEntries(historyFile);
    }

    public TimeSpan? GetResumePosition(string filePath, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(filePath) || duration <= TimeSpan.Zero)
            return null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(filePath, out var entry))
                return null;

            var position = TimeSpan.FromSeconds(entry.PositionSeconds);
            if (position < TimeSpan.FromSeconds(10))
                return null;

            if (duration - position < TimeSpan.FromSeconds(30))
                return null;

            return position;
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
                .Select(e => new RecentPlaybackItem(
                    e.Key,
                    Math.Max(0, e.Value.PositionSeconds),
                    Math.Max(0, e.Value.DurationSeconds),
                    e.Value.UpdatedUtc))
                .ToArray();
        }
    }

    public Dictionary<string, RecentPlaybackItem> GetResumeMap()
    {
        lock (_gate)
        {
            return _entries
                .Where(e => File.Exists(e.Key) && e.Value.PositionSeconds >= 10 && e.Value.DurationSeconds > 0 && e.Value.DurationSeconds - e.Value.PositionSeconds >= 30)
                .ToDictionary(
                    e => e.Key,
                    e => new RecentPlaybackItem(e.Key, Math.Max(0, e.Value.PositionSeconds), Math.Max(0, e.Value.DurationSeconds), e.Value.UpdatedUtc),
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Returns all entries with any recorded progress (position > 0 and duration > 0),
    /// including near-complete files. Used to show the progress bar/badge on cards.
    /// </summary>
    public Dictionary<string, RecentPlaybackItem> GetProgressMap()
    {
        lock (_gate)
        {
            return _entries
                .Where(e => File.Exists(e.Key) && e.Value.PositionSeconds > 0 && e.Value.DurationSeconds > 0)
                .ToDictionary(
                    e => e.Key,
                    e => new RecentPlaybackItem(e.Key, Math.Max(0, e.Value.PositionSeconds), Math.Max(0, e.Value.DurationSeconds), e.Value.UpdatedUtc),
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    public MoviePlaybackPreferences? GetPreferences(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(filePath, out var entry) || entry.Preferences is null)
                return null;

            return entry.Preferences;
        }
    }

    public void SavePosition(string filePath, TimeSpan position, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(filePath) || position < TimeSpan.FromSeconds(10))
            return;

        lock (_gate)
        {
            if (duration > TimeSpan.Zero && duration - position < TimeSpan.FromSeconds(30))
            {
                _entries.Remove(filePath);
            }
            else
            {
                _entries.TryGetValue(filePath, out var existingEntry);
                _entries[filePath] = new PlaybackHistoryEntry
                {
                    PositionSeconds = Math.Max(0, position.TotalSeconds),
                    DurationSeconds = Math.Max(0, duration.TotalSeconds),
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Preferences = existingEntry?.Preferences
                };
            }

            SaveEntries();
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
            _entries.TryGetValue(filePath, out var existingEntry);
            _entries[filePath] = new PlaybackHistoryEntry
            {
                PositionSeconds = existingEntry?.PositionSeconds ?? 0,
                DurationSeconds = existingEntry?.DurationSeconds ?? 0,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Preferences = preferences
            };

            SaveEntries();
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

    public void Trim(int historyLimit)
    {
        var normalizedLimit = Math.Max(1, historyLimit);

        lock (_gate)
        {
            if (_entries.Count <= normalizedLimit)
                return;

            var entriesToRemove = _entries
                .OrderByDescending(e => e.Value.UpdatedUtc)
                .Skip(normalizedLimit)
                .Select(e => e.Key)
                .ToArray();

            foreach (var filePath in entriesToRemove)
                _entries.Remove(filePath);

            SaveEntries();
        }
    }

    private static Dictionary<string, PlaybackHistoryEntry> LoadEntries(string historyFile)
    {
        try
        {
            if (!File.Exists(historyFile))
                return new Dictionary<string, PlaybackHistoryEntry>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(historyFile);
            var entries = JsonSerializer.Deserialize<Dictionary<string, PlaybackHistoryEntry>>(json);
            return entries is null
                ? new Dictionary<string, PlaybackHistoryEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, PlaybackHistoryEntry>(entries, StringComparer.OrdinalIgnoreCase);
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

            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyFile, json);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save playback history", ex);
        }
    }

    private sealed class PlaybackHistoryEntry
    {
        public double PositionSeconds { get; init; }
        public double DurationSeconds { get; init; }
        public DateTimeOffset UpdatedUtc { get; init; }
        public MoviePlaybackPreferences? Preferences { get; init; }
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
