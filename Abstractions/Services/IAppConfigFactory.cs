namespace RemotePlay.Abstractions.Services;

internal interface IAppConfigFactory
{
    AppConfig CreateForSettingsApply(
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
        int preferredDisplayIndex);

    AppConfig CreateForPlaybackPreferences(
        AppConfig currentConfig,
        double volume,
        double zoom,
        double audioBoost,
        double playbackSpeed,
        bool subtitlesEnabled);
}
