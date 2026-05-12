using RemotePlay.Abstractions.Services;

namespace RemotePlay.Services;

internal sealed class AppConfigFactory : IAppConfigFactory
{
    public AppConfig CreateForSettingsApply(
        AppConfig currentConfig,
        int port,
        bool useHttps,
        string moviesPath,
        double volume,
        double zoom,
        double audioBoost,
        double playbackSpeed,
        bool subtitlesEnabled,
        string preferredAudioLanguage,
        string preferredSubtitleLanguage,
        bool preferForcedSubtitles,
        PlaybackEndMode playbackEndBehavior,
        int preferredDisplayIndex)
    {
        ArgumentNullException.ThrowIfNull(currentConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(moviesPath);

        return new AppConfig
        {
            Port = port,
            UseHttps = useHttps,
            MoviesPath = moviesPath,
            Volume = volume,
            Brightness = 0.5,
            Zoom = zoom,
            AudioBoost = audioBoost,
            PlaybackSpeed = playbackSpeed,
            SubtitlesEnabled = subtitlesEnabled,
            PreferredAudioLanguage = preferredAudioLanguage,
            PreferredSubtitleLanguage = preferredSubtitleLanguage,
            PreferForcedSubtitles = preferForcedSubtitles,
            PlaybackEndBehavior = playbackEndBehavior,
            PreferredDisplayIndex = preferredDisplayIndex
        };
    }

    public AppConfig CreateForPlaybackPreferences(
        AppConfig currentConfig,
        double volume,
        double zoom,
        double audioBoost,
        double playbackSpeed,
        bool subtitlesEnabled)
    {
        ArgumentNullException.ThrowIfNull(currentConfig);

        return new AppConfig
        {
            Port = currentConfig.Port,
            UseHttps = currentConfig.UseHttps,
            MoviesPath = currentConfig.MoviesPath,
            Volume = volume,
            Brightness = 0.5,
            Zoom = zoom,
            AudioBoost = audioBoost,
            PlaybackSpeed = playbackSpeed,
            SubtitlesEnabled = subtitlesEnabled,
            PreferredAudioLanguage = currentConfig.PreferredAudioLanguage,
            PreferredSubtitleLanguage = currentConfig.PreferredSubtitleLanguage,
            PreferForcedSubtitles = currentConfig.PreferForcedSubtitles,
            PlaybackEndBehavior = currentConfig.PlaybackEndBehavior,
            PreferredDisplayIndex = currentConfig.PreferredDisplayIndex
        };
    }
}
