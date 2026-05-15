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
        bool useTrayIcon)
    {
        ArgumentNullException.ThrowIfNull(currentConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(moviesPath);

        return new AppConfig
        {
            Port = port,
            UseHttps = useHttps,
            MoviesPath = moviesPath,
            InstanceName = string.IsNullOrWhiteSpace(instanceName) ? currentConfig.InstanceName : instanceName.Trim(),
            Volume = volume,
            Brightness = 0.5,
            Zoom = zoom,
            AudioBoost = audioBoost,
            PlaybackSpeed = playbackSpeed,
            SubtitlesEnabled = subtitlesEnabled,
            PreferredAudioLanguage = preferredAudioLanguage,
            PreferredSubtitleLanguage = preferredSubtitleLanguage,
            SecondarySubtitleLanguage = secondarySubtitleLanguage,
            PreferForcedSubtitles = preferForcedSubtitles,
            PlaybackEndBehavior = playbackEndBehavior,
            PlaybackHistoryLimit = playbackHistoryLimit,
            LibraryRescanDelayMinutes = libraryRescanDelayMinutes,
            PreferredDisplayIndex = preferredDisplayIndex,
            StartWithWindows = startWithWindows,
            UseTrayIcon = useTrayIcon
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
            InstanceName = currentConfig.InstanceName,
            Volume = volume,
            Brightness = 0.5,
            Zoom = zoom,
            AudioBoost = audioBoost,
            PlaybackSpeed = playbackSpeed,
            SubtitlesEnabled = subtitlesEnabled,
            PreferredAudioLanguage = currentConfig.PreferredAudioLanguage,
            PreferredSubtitleLanguage = currentConfig.PreferredSubtitleLanguage,
            SecondarySubtitleLanguage = currentConfig.SecondarySubtitleLanguage,
            PreferForcedSubtitles = currentConfig.PreferForcedSubtitles,
            PlaybackEndBehavior = currentConfig.PlaybackEndBehavior,
            PlaybackHistoryLimit = currentConfig.PlaybackHistoryLimit,
            LibraryRescanDelayMinutes = currentConfig.LibraryRescanDelayMinutes,
            PreferredDisplayIndex = currentConfig.PreferredDisplayIndex,
            StartWithWindows = currentConfig.StartWithWindows,
            UseTrayIcon = currentConfig.UseTrayIcon
        };
    }
}
