using RemotePlay.Abstractions.Services;

namespace RemotePlay.Services;

internal sealed class AppConfigFactory : IAppConfigFactory
{
    public AppConfig CreateForSettingsApply(
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
        bool debugMode,
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
        NetworkShareCredential[] networkShareCredentials)
    {
        ArgumentNullException.ThrowIfNull(currentConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(moviesPath);

        return currentConfig with
        {
            Port                      = port,
            UseHttps                  = useHttps,
            MoviesPath                = moviesPath,
            MusicPath                 = string.IsNullOrWhiteSpace(musicPath) ? currentConfig.MusicPath : musicPath.Trim(),
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
            ExpertMode                = expertMode,
            DebugMode                 = debugMode,
            UpdateSourcePath          = updateSourcePath.Trim(),
            AutoUpdateIntervalMinutes = Math.Max(0, autoUpdateIntervalMinutes),
            MusicAudioDeviceId        = musicAudioDeviceId ?? string.Empty,
            AdditionalMoviesPaths     = additionalMoviesPaths ?? [],
            AdditionalMusicPaths      = additionalMusicPaths ?? [],
            EnableThumbnailGeneration = enableThumbnailGeneration,
            LibraryPageSize           = Math.Clamp(libraryPageSize, 25, 1000),
            IgnoredLibraryFolders     = ignoredLibraryFolders ?? [],
            VideoFileExtensions       = videoFileExtensions ?? [],
            MusicFileExtensions       = musicFileExtensions ?? [],
            MaxRequestsPerIpPerWindow = Math.Max(0, maxRequestsPerIpPerWindow),
            RateLimitWindowSeconds    = Math.Max(1, rateLimitWindowSeconds),
            NetworkShareCredentials   = networkShareCredentials ?? [],
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
