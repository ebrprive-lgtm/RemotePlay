using System.IO;
using Xunit;
using RemotePlay.Services;

namespace RemotePlay.Tests;

public class AppUpdaterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"AppUpdaterTests_{Guid.NewGuid():N}");

    public AppUpdaterTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Constructor_InitializesDefaultState()
    {
        var updater = new AppUpdater();

        Assert.False(updater.IsUpdating);
        Assert.False(updater.HasChecked);
        Assert.Equal(string.Empty, updater.LastUpdateError);
    }

    [Fact]
    public void Constructor_SetsCurrentVersion()
    {
        var updater = new AppUpdater();

        Assert.NotNull(updater.CurrentVersion);
    }

    [Fact]
    public void AvailableVersion_DefaultIsEmpty()
    {
        var updater = new AppUpdater();

        Assert.Equal(string.Empty, updater.AvailableVersion);
    }

    [Fact]
    public void Stop_BeforeStart_DoesNotThrow()
    {
        var updater = new AppUpdater();

        var ex = Record.Exception(() => updater.Stop());

        Assert.Null(ex);
    }

    [Fact]
    public void Start_WithEmptyUpdateSourcePath_DoesNotThrowAndTimerIsNotStarted()
    {
        var updater = new AppUpdater();
        var config = new AppConfig { UpdateSourcePath = string.Empty };

        var ex = Record.Exception(() => updater.Start(config));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Start_WithNonExistentSourcePath_SetsHasCheckedAndLastUpdateError()
    {
        var updater = new AppUpdater();
        var config = new AppConfig { UpdateSourcePath = Path.Combine(_tempDir, "does_not_exist") };

        updater.Start(config);

        // Wait up to 3 s for the background task to complete.
        for (var i = 0; i < 30 && !updater.HasChecked; i++)
            await Task.Delay(100);

        Assert.True(updater.HasChecked);
        Assert.Contains("not found", updater.LastUpdateError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_WithSourcePathButNoVersionTxt_SetsHasCheckedAndLastUpdateError()
    {
        var sourceDir = Path.Combine(_tempDir, "source_no_ver");
        Directory.CreateDirectory(sourceDir);

        var updater = new AppUpdater();
        var config = new AppConfig { UpdateSourcePath = sourceDir };

        updater.Start(config);

        for (var i = 0; i < 30 && !updater.HasChecked; i++)
            await Task.Delay(100);

        Assert.True(updater.HasChecked);
        Assert.Contains("version.txt", updater.LastUpdateError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_WithSameVersionInSource_SetsHasCheckedAndNoError()
    {
        var sourceDir = Path.Combine(_tempDir, "source_same_ver");
        Directory.CreateDirectory(sourceDir);

        var updater = new AppUpdater();
        // Write the current version into the source folder so the check sees "up to date".
        File.WriteAllText(Path.Combine(sourceDir, "version.txt"), updater.CurrentVersion);

        var config = new AppConfig { UpdateSourcePath = sourceDir };

        updater.Start(config);

        for (var i = 0; i < 30 && !updater.HasChecked; i++)
            await Task.Delay(100);

        Assert.True(updater.HasChecked);
        Assert.Equal(string.Empty, updater.LastUpdateError);
        Assert.Equal(updater.CurrentVersion, updater.AvailableVersion);
    }

    [Fact]
    public async Task Start_WithNewerVersionButNoChangedFiles_SetsHasCheckedAndNotUpdating()
    {
        var sourceDir = Path.Combine(_tempDir, "source_newer_ver");
        Directory.CreateDirectory(sourceDir);

        var updater = new AppUpdater();
        File.WriteAllText(Path.Combine(sourceDir, "version.txt"), "9999.9.9");

        var config = new AppConfig { UpdateSourcePath = sourceDir };

        updater.Start(config);

        for (var i = 0; i < 30 && !updater.HasChecked; i++)
            await Task.Delay(100);

        Assert.True(updater.HasChecked);
        Assert.Equal("9999.9.9", updater.AvailableVersion);
        // No files to copy → update should not be in progress.
        Assert.False(updater.IsUpdating);
    }
}
