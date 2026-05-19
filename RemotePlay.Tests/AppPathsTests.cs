using System.IO;
using Xunit;

namespace RemotePlay.Tests;

public class AppPathsTests
{
    [Fact]
    public void UserDataDirectory_IsUnderApplicationData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(appData, AppPaths.UserDataDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserDataDirectory_EndsWithRemotePlay()
    {
        Assert.EndsWith("RemotePlay", AppPaths.UserDataDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigFile_IsUnderUserDataDirectory()
    {
        Assert.StartsWith(AppPaths.UserDataDirectory, AppPaths.ConfigFile, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("remoteplay.json", Path.GetFileName(AppPaths.ConfigFile));
    }

    [Fact]
    public void HistoryFile_IsUnderUserDataDirectory()
    {
        Assert.StartsWith(AppPaths.UserDataDirectory, AppPaths.HistoryFile, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("playback-history.json", Path.GetFileName(AppPaths.HistoryFile));
    }

    [Fact]
    public void ThumbnailCacheDirectory_IsUnderUserDataDirectory()
    {
        Assert.StartsWith(AppPaths.UserDataDirectory, AppPaths.ThumbnailCacheDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("thumbnail-cache", Path.GetFileName(AppPaths.ThumbnailCacheDirectory));
    }

    [Fact]
    public void LibraryIndexCacheFile_IsUnderUserDataDirectory()
    {
        Assert.StartsWith(AppPaths.UserDataDirectory, AppPaths.LibraryIndexCacheFile, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("library-index.json", Path.GetFileName(AppPaths.LibraryIndexCacheFile));
    }

    [Fact]
    public void FavoritesFile_IsUnderUserDataDirectory()
    {
        Assert.StartsWith(AppPaths.UserDataDirectory, AppPaths.FavoritesFile, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("favorites.json", Path.GetFileName(AppPaths.FavoritesFile));
    }

    [Fact]
    public void LogFile_IsUnderUserDataDirectory()
    {
        Assert.StartsWith(AppPaths.UserDataDirectory, AppPaths.LogFile, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("remoteplay.log", Path.GetFileName(AppPaths.LogFile));
    }
}
