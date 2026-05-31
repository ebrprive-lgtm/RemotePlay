namespace RemotePlay.Abstractions.Services;

internal interface IAppConfigFactory
{
    AppConfig CreateForSettingsApply(
        AppConfig currentConfig,
        int port,
        bool useHttps,
        string moviesPath,
        string musicPath,
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
        bool expertMode,
        string updateSourcePath,
        int autoUpdateIntervalMinutes,
        string musicAudioDeviceId,
        string[] additionalMoviesPaths,
        string[] additionalMusicPaths,
        bool enableThumbnailGeneration,
        int libraryPageSize,
        string[] ignoredLibraryFolders,
        string[] videoFileExtensions,
        string[] musicFileExtensions,
        int maxRequestsPerIpPerWindow,
        int rateLimitWindowSeconds,
        NetworkShareCredential[] networkShareCredentials);

    AppConfig CreateForPlaybackPreferences(
        AppConfig currentConfig,
        double volume,
        double brightness,
        double zoom,
        double audioBoost,
        double playbackSpeed,
        bool subtitlesEnabled);
}
