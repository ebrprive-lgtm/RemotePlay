namespace RemotePlay.Abstractions.Services;

internal interface IAppConfigFactory
{
    AppConfig CreateForSettingsApply(
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
        int autoUpdateIntervalMinutes);

    AppConfig CreateForPlaybackPreferences(
        AppConfig currentConfig,
        double volume,
        double brightness,
        double zoom,
        double audioBoost,
        double playbackSpeed,
        bool subtitlesEnabled);
}
