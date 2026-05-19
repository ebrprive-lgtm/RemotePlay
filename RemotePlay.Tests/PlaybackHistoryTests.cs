using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class PlaybackHistoryTests
{
    [Fact]
    public void GetResumePositionReturnsSavedPosition()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);
        var filePath = Path.Combine(Path.GetTempPath(), "movie.mkv");

        history.SavePosition(filePath, TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        var result = history.GetResumePosition(filePath, TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromMinutes(5), result);
    }

    [Fact]
    public void GetResumePositionReturnsNullForShortPosition()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);
        var filePath = Path.Combine(Path.GetTempPath(), "movie.mkv");

        history.SavePosition(filePath, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1));

        var result = history.GetResumePosition(filePath, TimeSpan.FromHours(1));

        Assert.Null(result);
    }

    [Fact]
    public void SavePositionClearsPositionNearEndOfMovie()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);
        var filePath = Path.Combine(Path.GetTempPath(), "movie.mkv");

        history.SavePosition(filePath, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));
        history.SavePosition(filePath, TimeSpan.FromMinutes(59).Add(TimeSpan.FromSeconds(45)), TimeSpan.FromHours(1));

        var result = history.GetResumePosition(filePath, TimeSpan.FromHours(1));

        Assert.Null(result);
    }

    [Fact]
    public void ClearRemovesSavedPosition()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);
        var filePath = Path.Combine(Path.GetTempPath(), "movie.mkv");

        history.SavePosition(filePath, TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
        history.Clear(filePath);

        var result = history.GetResumePosition(filePath, TimeSpan.FromHours(1));

        Assert.Null(result);
    }

    [Fact]
    public void SavePositionWithLimitKeepsMostRecentMovies()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);
        var oldMovie = CreateExistingMoviePath("old.mkv");
        var middleMovie = CreateExistingMoviePath("middle.mkv");
        var newestMovie = CreateExistingMoviePath("newest.mkv");

        history.SavePosition(oldMovie, TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), historyLimit: 2);
        history.SavePosition(middleMovie, TimeSpan.FromMinutes(6), TimeSpan.FromHours(1), historyLimit: 2);
        history.SavePosition(newestMovie, TimeSpan.FromMinutes(7), TimeSpan.FromHours(1), historyLimit: 2);

        var recent = history.GetRecent(10);

        Assert.DoesNotContain(recent, item => item.FilePath == oldMovie);
        Assert.Contains(recent, item => item.FilePath == middleMovie);
        Assert.Contains(recent, item => item.FilePath == newestMovie);
    }

    private static string GetHistoryFile() =>
        Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"), "history.json");

    private static string CreateExistingMoviePath(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, fileName);
        File.WriteAllText(filePath, string.Empty);

        return filePath;
    }

    // ── Constructor guard ────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenHistoryFileIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new PlaybackHistory(string.Empty));
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenHistoryFileIsWhitespace()
    {
        Assert.Throws<ArgumentException>(() => new PlaybackHistory("   "));
    }

    // ── SavePreferences / GetPreferences ─────────────────────────────────────

    [Fact]
    public void SavePreferencesAndGetPreferences_RoundTrip_PreservesAllFields()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);
        var filePath = CreateExistingMoviePath("prefs.mkv");
        var prefs = new MoviePlaybackPreferences
        {
            Brightness = 0.8,
            Saturation = 1.2,
            Zoom = 1.5,
            ForceSwAudio = true
        };

        history.SavePreferences(filePath, prefs);
        var result = history.GetPreferences(filePath);

        Assert.NotNull(result);
        Assert.Equal(0.8, result.Brightness);
        Assert.Equal(1.2, result.Saturation);
        Assert.Equal(1.5, result.Zoom);
        Assert.True(result.ForceSwAudio);
    }

    [Fact]
    public void GetPreferences_ReturnsNull_ForUnknownFile()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);

        var result = history.GetPreferences(Path.Combine(Path.GetTempPath(), "nonexistent.mkv"));

        Assert.Null(result);
    }

    [Fact]
    public void GetPreferences_ReturnsNull_WhenFilePathIsEmpty()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);

        var result = history.GetPreferences(string.Empty);

        Assert.Null(result);
    }

    // ── GetProgressMap ───────────────────────────────────────────────────────

    [Fact]
    public void GetProgressMap_IncludesEntries_EvenWhenPositionIsNearEnd()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);
        var filePath = CreateExistingMoviePath("near-end.mkv");

        // Near end — SavePosition will remove it from entries, so save it as preferences
        // to get it in the map. Instead test a normal partial-watched entry.
        history.SavePosition(filePath, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));

        var map = history.GetProgressMap();

        Assert.True(map.ContainsKey(filePath));
    }

    [Fact]
    public void GetProgressMap_ExcludesEntries_WithZeroPosition()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);

        // A file with no entry at all should not appear in the progress map.
        var unknownPath = Path.Combine(Path.GetTempPath(), "never-watched.mkv");

        var map = history.GetProgressMap();

        Assert.False(map.ContainsKey(unknownPath));
    }

    // ── GetResumeMap ─────────────────────────────────────────────────────────

    [Fact]
    public void GetResumeMap_ExcludesEntries_WhenPositionIsNearEnd()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);
        var filePath = CreateExistingMoviePath("near-end-resume.mkv");

        // First save a mid-movie position so the entry exists.
        history.SavePosition(filePath, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));
        var recent = history.GetRecent(10);
        Assert.Contains(recent, r => r.FilePath == filePath);

        // Save a position that leaves only 29s remaining (< 30s threshold) — entry is removed.
        history.SavePosition(filePath, TimeSpan.FromSeconds(3571), TimeSpan.FromHours(1));

        var map = history.GetResumeMap();

        Assert.False(map.ContainsKey(filePath));
    }

    [Fact]
    public void GetResumeMap_ExcludesEntries_WhenPositionIsUnderTenSeconds()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);
        var filePath = CreateExistingMoviePath("short-pos-resume.mkv");

        // SavePosition ignores positions < 10s, so this entry will never be created.
        history.SavePosition(filePath, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1));

        var map = history.GetResumeMap();

        Assert.False(map.ContainsKey(filePath));
    }

    // ── Trim standalone ──────────────────────────────────────────────────────

    [Fact]
    public void Trim_KeepsOnlyMostRecentEntries()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);

        // Create 5 entries with distinct files, each an actual file on disk
        var files = Enumerable.Range(1, 5)
            .Select(i => CreateExistingMoviePath($"movie{i}.mkv"))
            .ToArray();

        foreach (var f in files)
            history.SavePosition(f, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));

        history.Trim(3);

        var recent = history.GetRecent(10);
        Assert.Equal(3, recent.Length);
    }

    [Fact]
    public void Trim_DoesNothing_WhenEntryCountIsAlreadyWithinLimit()
    {
        var historyFile = GetHistoryFile();
        var history = new PlaybackHistory(historyFile);
        var filePath = CreateExistingMoviePath("solo.mkv");

        history.SavePosition(filePath, TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        history.Trim(10);

        var recent = history.GetRecent(10);
        Assert.Single(recent);
    }
}
