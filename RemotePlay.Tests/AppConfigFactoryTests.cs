using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

public sealed class AppConfigFactoryTests
{
    [Fact]
    public void CreateForSettingsApplyBuildsExpectedConfig()
    {
        var factory = new AppConfigFactory();
        var current = new AppConfig();

        var config = factory.CreateForSettingsApply(
            current,
            port: 5000,
            useHttps: true,
            moviesPath: "C:\\Movies",
            musicPath: string.Empty,
            instanceName: "Living Room",
            volume: 0.8,
            zoom: 1.15,
            audioBoost: 1.2,
            playbackSpeed: 1.1,
            subtitlesEnabled: false,
            preferredAudioLanguage: "eng",
            preferredSubtitleLanguage: "fra",
            secondarySubtitleLanguage: "deu",
            preferForcedSubtitles: true,
            playbackEndBehavior: PlaybackEndMode.PlayNext,
            playbackHistoryLimit: 9,
            libraryRescanDelayMinutes: 12,
            preferredDisplayIndex: 2,
            startWithWindows: true,
            useTrayIcon: false,
            updateSourcePath: @"C:\Updates",
            autoUpdateIntervalMinutes: 30,
            musicAudioDeviceId: string.Empty,
            additionalMoviesPaths: [],
            additionalMusicPaths: [],
            enableThumbnailGeneration: false,
            libraryPageSize: 100,
            ignoredLibraryFolders: [],
            videoFileExtensions: [],
            musicFileExtensions: [],
            maxRequestsPerIpPerWindow: 0,
            rateLimitWindowSeconds: 60,
            networkShareCredentials: []);

        Assert.Equal(5000, config.Port);
        Assert.True(config.UseHttps);
        Assert.Equal("C:\\Movies", config.MoviesPath);
        Assert.Equal(0.8, config.Volume);
        Assert.Equal(1.2, config.AudioBoost);
        Assert.Equal(1.1, config.PlaybackSpeed);
        Assert.Equal(1.15, config.Zoom);
        Assert.False(config.SubtitlesEnabled);
        Assert.Equal("eng", config.PreferredAudioLanguage);
        Assert.Equal("fra", config.PreferredSubtitleLanguage);
        Assert.Equal("deu", config.SecondarySubtitleLanguage);
        Assert.True(config.PreferForcedSubtitles);
        Assert.Equal(PlaybackEndMode.PlayNext, config.PlaybackEndBehavior);
        Assert.Equal(9, config.PlaybackHistoryLimit);
        Assert.Equal(12, config.LibraryRescanDelayMinutes);
        Assert.Equal(2, config.PreferredDisplayIndex);
        Assert.True(config.StartWithWindows);
        Assert.False(config.UseTrayIcon);
        Assert.Equal(0.5, config.Brightness);
        Assert.Equal("Living Room", config.InstanceName);
        Assert.Equal(@"C:\Updates", config.UpdateSourcePath);
        Assert.Equal(30, config.AutoUpdateIntervalMinutes);
    }

    [Fact]
    public void CreateForPlaybackPreferencesPreservesExistingSettingsAndUpdatesPlaybackValues()
    {
        var factory = new AppConfigFactory();
        var current = new AppConfig
        {
            Port = 6000,
            UseHttps = true,
            MoviesPath = "D:\\Media",
            Zoom = 1.2,
            PreferredAudioLanguage = "jpn",
            PreferredSubtitleLanguage = "eng",
            SecondarySubtitleLanguage = "fra",
            PreferForcedSubtitles = false,
            PlaybackEndBehavior = PlaybackEndMode.ReturnToLibrary,
            PlaybackHistoryLimit = 11,
            PreferredDisplayIndex = 1,
            StartWithWindows = true,
            UseTrayIcon = false
        };

        var config = factory.CreateForPlaybackPreferences(
            current,
            volume: 0.6,
            brightness: 0.7,
            zoom: 1.35,
            audioBoost: 1.5,
            playbackSpeed: 1.25,
            subtitlesEnabled: true);

        Assert.Equal(6000, config.Port);
        Assert.True(config.UseHttps);
        Assert.Equal("D:\\Media", config.MoviesPath);
        Assert.Equal(0.6, config.Volume);
        Assert.Equal(1.5, config.AudioBoost);
        Assert.Equal(1.25, config.PlaybackSpeed);
        Assert.Equal(1.35, config.Zoom);
        Assert.True(config.SubtitlesEnabled);
        Assert.Equal("jpn", config.PreferredAudioLanguage);
        Assert.Equal("eng", config.PreferredSubtitleLanguage);
        Assert.Equal("fra", config.SecondarySubtitleLanguage);
        Assert.False(config.PreferForcedSubtitles);
        Assert.Equal(PlaybackEndMode.ReturnToLibrary, config.PlaybackEndBehavior);
        Assert.Equal(11, config.PlaybackHistoryLimit);
        Assert.Equal(1, config.PreferredDisplayIndex);
        Assert.True(config.StartWithWindows);
        Assert.False(config.UseTrayIcon);
        Assert.Equal(0.7, config.Brightness);
    }

    // ── Guard clauses

    [Fact]
    public void CreateForSettingsApply_ThrowsArgumentNullException_WhenCurrentConfigIsNull()
    {
        var factory = new AppConfigFactory();

        Assert.Throws<ArgumentNullException>(() =>
            factory.CreateForSettingsApply(
                null!, 5000, false, @"C:\Movies", string.Empty, "Test", 1, 1, 1, 1,
                true, "eng", "eng", string.Empty, true,
                PlaybackEndMode.Stop, 7, 10, -1, false, true, string.Empty, 60, string.Empty,
                [], [], false, 100, [], [], [], 0, 60, []));
    }

    [Fact]
    public void CreateForSettingsApply_ThrowsArgumentException_WhenMoviesPathIsWhitespace()
    {
        var factory = new AppConfigFactory();
        var current = new AppConfig();

        Assert.Throws<ArgumentException>(() =>
            factory.CreateForSettingsApply(
                current, 5000, false, "   ", string.Empty, "Test", 1, 1, 1, 1,
                true, "eng", "eng", string.Empty, true,
                PlaybackEndMode.Stop, 7, 10, -1, false, true, string.Empty, 60, string.Empty,
                [], [], false, 100, [], [], [], 0, 60, []));
    }

    // ── InstanceName fallback ────────────────────────────────────────────────

    [Fact]
    public void CreateForSettingsApply_UsesCurrentInstanceName_WhenInstanceNameIsBlankOrWhitespace()
    {
        var factory = new AppConfigFactory();
        var current = new AppConfig { InstanceName = "My Server" };

        var config = factory.CreateForSettingsApply(
            current, 5000, false, @"C:\Movies", string.Empty, "   ", 1, 1, 1, 1,
            true, "eng", "eng", string.Empty, true,
            PlaybackEndMode.Stop, 7, 10, -1, false, true, string.Empty, 60, string.Empty,
            [], [], false, 100, [], [], [], 0, 60, []);

        Assert.Equal("My Server", config.InstanceName);
    }

    // ── autoUpdateIntervalMinutes clamp ──────────────────────────────────────

    [Fact]
    public void CreateForSettingsApply_ClampsAutoUpdateIntervalMinutesToZero_WhenNegative()
    {
        var factory = new AppConfigFactory();
        var current = new AppConfig();

        var config = factory.CreateForSettingsApply(
            current, 5000, false, @"C:\Movies", string.Empty, "Test", 1, 1, 1, 1,
            true, "eng", "eng", string.Empty, true,
            PlaybackEndMode.Stop, 7, 10, -1, false, true, string.Empty, -99, string.Empty,
            [], [], false, 100, [], [], [], 0, 60, []);

        Assert.Equal(0, config.AutoUpdateIntervalMinutes);
    }

    [Fact]
    public void CreateForSettingsApply_KeepsAutoUpdateIntervalMinutesAtZero_WhenPassedZero()
    {
        var factory = new AppConfigFactory();
        var current = new AppConfig();

        var config = factory.CreateForSettingsApply(
            current, 5000, false, @"C:\Movies", string.Empty, "Test", 1, 1, 1, 1,
            true, "eng", "eng", string.Empty, true,
            PlaybackEndMode.Stop, 7, 10, -1, false, true, string.Empty, 0, string.Empty,
            [], [], false, 100, [], [], [], 0, 60, []);

        Assert.Equal(0, config.AutoUpdateIntervalMinutes);
    }
}
