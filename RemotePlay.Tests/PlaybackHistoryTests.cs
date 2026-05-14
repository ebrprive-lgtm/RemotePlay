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
}
