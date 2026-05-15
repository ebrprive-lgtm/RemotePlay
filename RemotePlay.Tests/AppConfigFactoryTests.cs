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
            preferredDisplayIndex: 2);

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
        Assert.Equal(2, config.PreferredDisplayIndex);
        Assert.Equal(0.5, config.Brightness);
        Assert.Equal("Living Room", config.InstanceName);
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
            PreferredDisplayIndex = 1
        };

        var config = factory.CreateForPlaybackPreferences(
            current,
            volume: 0.6,
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
        Assert.Equal(0.5, config.Brightness);
    }
}
