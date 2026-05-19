using System.IO;
using System.Text.Json;
using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class AppConfigTests : IDisposable
{
    // Back up and restore the real config file so these tests never corrupt user data.
    private readonly string _configFile = AppPaths.ConfigFile;
    private readonly byte[]? _originalConfig;

    public AppConfigTests()
    {
        try
        {
            if (File.Exists(_configFile))
                _originalConfig = File.ReadAllBytes(_configFile);
        }
        catch (IOException)
        {
            // File is locked by the running app — skip backup; tests that write the file will also skip.
        }
    }

    public void Dispose()
    {
        try
        {
            if (_originalConfig is not null)
                File.WriteAllBytes(_configFile, _originalConfig);
            else if (File.Exists(_configFile))
                File.Delete(_configFile);
        }
        catch (IOException)
        {
            // File is locked by the running app — restore will happen on next test run.
        }
    }

    // ── Scheme ──────────────────────────────────────────────────────────────

    [Fact]
    public void Scheme_ReturnsHttp_WhenUseHttpsIsFalse()
    {
        var config = new AppConfig { UseHttps = false };

        Assert.Equal("http", config.Scheme);
    }

    [Fact]
    public void Scheme_ReturnsHttps_WhenUseHttpsIsTrue()
    {
        var config = new AppConfig { UseHttps = true };

        Assert.Equal("https", config.Scheme);
    }

    // ── EffectiveLibraryPageSize ─────────────────────────────────────────────

    [Fact]
    public void EffectiveLibraryPageSize_ClampsToMin25_WhenValueIsTooSmall()
    {
        var config = new AppConfig { LibraryPageSize = 5 };

        Assert.Equal(25, config.EffectiveLibraryPageSize);
    }

    [Fact]
    public void EffectiveLibraryPageSize_ReturnsValue_WhenInRange()
    {
        var config = new AppConfig { LibraryPageSize = 200 };

        Assert.Equal(200, config.EffectiveLibraryPageSize);
    }

    // ── EffectiveVideoFileExtensions ─────────────────────────────────────────

    [Fact]
    public void EffectiveVideoFileExtensions_AddsDotPrefix_WhenMissing()
    {
        var config = new AppConfig { VideoFileExtensions = ["mp4"] };

        Assert.Contains(".mp4", config.EffectiveVideoFileExtensions);
    }

    [Fact]
    public void EffectiveVideoFileExtensions_FallsBackToDefaults_WhenArrayIsEmpty()
    {
        var config = new AppConfig { VideoFileExtensions = [] };
        var defaults = new AppConfig().VideoFileExtensions;

        Assert.Equal(defaults.Length, config.EffectiveVideoFileExtensions.Length);
    }

    [Fact]
    public void EffectiveVideoFileExtensions_DeduplicatesEntries()
    {
        var config = new AppConfig { VideoFileExtensions = [".mp4", "mp4", ".MP4"] };

        Assert.Single(config.EffectiveVideoFileExtensions);
    }

    // ── EffectiveIgnoredLibraryFolders ───────────────────────────────────────

    [Fact]
    public void EffectiveIgnoredLibraryFolders_TrimsWhitespace()
    {
        var config = new AppConfig { IgnoredLibraryFolders = [" Subs "] };

        Assert.Contains("Subs", config.EffectiveIgnoredLibraryFolders);
    }

    [Fact]
    public void EffectiveIgnoredLibraryFolders_DeduplicatesCaseInsensitively()
    {
        var config = new AppConfig { IgnoredLibraryFolders = ["Subs", "subs", "SUBS"] };

        Assert.Single(config.EffectiveIgnoredLibraryFolders);
    }

    [Fact]
    public void EffectiveIgnoredLibraryFolders_FallsBackToDefaults_WhenArrayIsEmpty()
    {
        var config = new AppConfig { IgnoredLibraryFolders = [] };
        var defaults = new AppConfig().IgnoredLibraryFolders;

        Assert.Equal(defaults.Length, config.EffectiveIgnoredLibraryFolders.Length);
    }

    // ── ResolvedMoviesPath ───────────────────────────────────────────────────

    [Fact]
    public void ResolvedMoviesPath_ReturnsPath_WhenAlreadyRooted()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "MyMovies");
        var config = new AppConfig { MoviesPath = rooted };

        Assert.Equal(rooted, config.ResolvedMoviesPath);
    }

    [Fact]
    public void ResolvedMoviesPath_ResolvesRelativePath_AgainstBaseDirectory()
    {
        const string relative = "Movies";
        var config = new AppConfig { MoviesPath = relative };

        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));

        Assert.Equal(expected, config.ResolvedMoviesPath);
    }

    // ── WithBrowserDirs ──────────────────────────────────────────────────────

    [Fact]
    public void WithBrowserDirs_UpdatesOnlyBrowserDirFields()
    {
        var source = new AppConfig
        {
            Port = 7000,
            MoviesPath = @"C:\Movies",
            LinkBrowserLeftDir = "old-left",
            LinkBrowserRightDir = "old-right"
        };

        var result = AppConfig.WithBrowserDirs(source, "new-left", "new-right");

        Assert.Equal("new-left", result.LinkBrowserLeftDir);
        Assert.Equal("new-right", result.LinkBrowserRightDir);
        Assert.Equal(source.Port, result.Port);
        Assert.Equal(source.MoviesPath, result.MoviesPath);
        Assert.Equal(source.InstanceId, result.InstanceId);
        Assert.Equal(source.Volume, result.Volume);
        Assert.Equal(source.PlaybackHistoryLimit, result.PlaybackHistoryLimit);
    }

    [Fact]
    public void WithBrowserDirs_ThrowsArgumentNullException_WhenSourceIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AppConfig.WithBrowserDirs(null!, "left", "right"));
    }

    // ── Save / Load round-trip ───────────────────────────────────────────────

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesConfigValues()
    {
        var config = new AppConfig
        {
            Port = 8877,
            UseHttps = true,
            MoviesPath = @"C:\TestMovies",
            InstanceName = "Test Instance",
            Volume = 0.75,
            AudioBoost = 1.3,
            PlaybackSpeed = 1.5,
            SubtitlesEnabled = false,
            PlaybackHistoryLimit = 12
        };

        try
        {
            AppConfig.Save(config);
        }
        catch (IOException)
        {
            return; // Config file locked by running app — skip.
        }

        var loaded = AppConfig.Load();

        Assert.Equal(config.Port, loaded.Port);
        Assert.Equal(config.UseHttps, loaded.UseHttps);
        Assert.Equal(config.MoviesPath, loaded.MoviesPath);
        Assert.Equal(config.InstanceName, loaded.InstanceName);
        Assert.Equal(config.Volume, loaded.Volume);
        Assert.Equal(config.AudioBoost, loaded.AudioBoost);
        Assert.Equal(config.PlaybackSpeed, loaded.PlaybackSpeed);
        Assert.Equal(config.SubtitlesEnabled, loaded.SubtitlesEnabled);
        Assert.Equal(config.PlaybackHistoryLimit, loaded.PlaybackHistoryLimit);
    }

    [Fact]
    public void Load_ReturnsDefaultConfig_WhenFileContainsInvalidJson()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configFile)!);
        try
        {
            File.WriteAllText(_configFile, "{ this is not valid json }}}");
        }
        catch (IOException)
        {
            return; // Config file locked by running app — skip.
        }

        var result = AppConfig.Load();

        Assert.NotNull(result);
        Assert.Equal(new AppConfig().Port, result.Port);
        Assert.Equal(new AppConfig().Volume, result.Volume);
    }

    [Fact]
    public void Load_ReturnsDefaultConfig_WhenFileDoesNotExist()
    {
        try
        {
            if (File.Exists(_configFile))
                File.Delete(_configFile);
        }
        catch (IOException)
        {
            return; // Config file locked by running app — skip.
        }

        var result = AppConfig.Load();

        Assert.NotNull(result);
        Assert.Equal(new AppConfig().Port, result.Port);
    }
}
