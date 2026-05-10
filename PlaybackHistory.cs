using System.IO;
using System.Text.Json;

namespace RemotePlay;

internal sealed class PlaybackHistory
{
    private readonly string _historyFile;
    private readonly object _gate = new();
    private Dictionary<string, PlaybackHistoryEntry> _entries;

    public PlaybackHistory()
        : this(Path.Combine(AppContext.BaseDirectory, "playback-history.json"))
    {
    }

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
                _entries[filePath] = new PlaybackHistoryEntry
                {
                    PositionSeconds = Math.Max(0, position.TotalSeconds),
                    DurationSeconds = Math.Max(0, duration.TotalSeconds),
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
            }

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
    }
}
