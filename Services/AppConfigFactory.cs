using RemotePlay.Abstractions.Services;

namespace RemotePlay.Services;

internal sealed class AppConfigFactory : IAppConfigFactory
{
    public AppConfig CreateForSettingsApply(
        AppConfig currentConfig,
        int port,
        bool useHttps,
        string moviesPath,
        string instanceName,
        double volume,
        double zoom,
        double audioBoost,
        double playbackSpeed,
        bool subtitlesEnabled,
        string preferredAudioLanguage,
        string preferredSubtitleLanguage,
        string secondarySubtitleLanguage,
        bool preferForcedSubtitles,
        PlaybackEndMode playbackEndBehavior,
        int playbackHistoryLimit,
        int libraryRescanDelayMinutes,
        int preferredDisplayIndex,
        bool startWithWindows,
        bool useTrayIcon,
        string updateSourcePath,
        int autoUpdateIntervalMinutes)
    {
        ArgumentNullException.ThrowIfNull(currentConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(moviesPath);

        return currentConfig with
        {
            Port                      = port,
            UseHttps                  = useHttps,
            MoviesPath                = moviesPath,
            InstanceName              = string.IsNullOrWhiteSpace(instanceName) ? currentConfig.InstanceName : instanceName.Trim(),
            Volume                    = volume,
            Zoom                      = zoom,
            AudioBoost                = audioBoost,
            PlaybackSpeed             = playbackSpeed,
            SubtitlesEnabled          = subtitlesEnabled,
            PreferredAudioLanguage    = preferredAudioLanguage,
            PreferredSubtitleLanguage = preferredSubtitleLanguage,
            SecondarySubtitleLanguage = secondarySubtitleLanguage,
            PreferForcedSubtitles     = preferForcedSubtitles,
            PlaybackEndBehavior       = playbackEndBehavior,
            PlaybackHistoryLimit      = playbackHistoryLimit,
            LibraryRescanDelayMinutes = libraryRescanDelayMinutes,
            PreferredDisplayIndex     = preferredDisplayIndex,
            StartWithWindows          = startWithWindows,
            UseTrayIcon               = useTrayIcon,
            UpdateSourcePath          = updateSourcePath.Trim(),
            AutoUpdateIntervalMinutes = Math.Max(0, autoUpdateIntervalMinutes),
        };
    }

    public AppConfig CreateForPlaybackPreferences(
        AppConfig currentConfig,
        double volume,
        double brightness,
        double zoom,
        double audioBoost,
        double playbackSpeed,
        bool subtitlesEnabled)
    {
        ArgumentNullException.ThrowIfNull(currentConfig);

        return currentConfig with
        {
            Volume           = volume,
            Brightness       = brightness,
            Zoom             = zoom,
            AudioBoost       = audioBoost,
            PlaybackSpeed    = playbackSpeed,
            SubtitlesEnabled = subtitlesEnabled,
        };
    }
}
