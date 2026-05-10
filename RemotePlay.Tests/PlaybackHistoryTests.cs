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

    private static string GetHistoryFile() =>
        Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"), "history.json");
}
