using System.Collections.Generic;
using System.IO;

namespace RemotePlay;

/// <summary>
/// Credentials for a specific UNC network share (e.g. <c>\\\\server\\share</c>).
/// The <see cref="Path"/> value is matched as a prefix against the paths being scanned.
/// </summary>
internal sealed record NetworkShareCredential
{
    public string Path { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

internal sealed record AppConfig
{
    public int Port { get; init; } = 9090;
    public bool UseHttps { get; init; }
    /// <summary>Stable identifier for this instance. Used to deduplicate peer entries when a machine has multiple network interfaces.</summary>
    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Friendly display name shown in the peer discovery list. Defaults to the machine hostname.</summary>
    public string InstanceName { get; init; } = System.Net.Dns.GetHostName();
    public string MoviesPath { get; init; } = Path.Combine(AppPaths.UserDataDirectory, "Movies");
    /// <summary>Root folder that contains the music library (MP3, FLAC, etc.).</summary>
    public string MusicPath { get; init; } = Path.Combine(AppPaths.UserDataDirectory, "Music");
    public double Volume { get; init; } = 1;
    public double Brightness { get; init; } = 0.5;
    public double Zoom { get; init; } = 1;
    public double AudioBoost { get; init; } = 1;
    public double PlaybackSpeed { get; init; } = 1;
    public bool SubtitlesEnabled { get; init; } = true;
    public string PreferredAudioLanguage { get; init; } = "eng";
    public string PreferredSubtitleLanguage { get; init; } = "eng";
    public string SecondarySubtitleLanguage { get; init; } = string.Empty;
    public bool PreferForcedSubtitles { get; init; } = true;
    public PlaybackEndMode PlaybackEndBehavior { get; init; } = PlaybackEndMode.Stop;
    public int PlaybackHistoryLimit { get; init; } = 7;
    public int PreferredDisplayIndex { get; init; } = -1; // -1 = primary screen
    public bool StartWithWindows { get; init; }
    public bool UseTrayIcon { get; init; } = true;
    /// <summary>When true, expert-only controls (e.g. dynamic folder creation) are visible in the web UI.</summary>
    public bool ExpertMode { get; init; }
    /// <summary>When true, debug-only controls (e.g. Reset M3U cache) are visible in the web UI.</summary>
    public bool DebugMode { get; init; }
    public int LibraryRescanDelayMinutes { get; init; } = 60;
    public bool EnableThumbnailGeneration { get; init; } = true;
    public string[] IgnoredLibraryFolders { get; init; } = ["Subs", "Alt"];
    public string[] VideoFileExtensions { get; init; } = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv"];
    /// <summary>Audio file extensions recognised by the music library scanner.</summary>
    public string[] MusicFileExtensions { get; init; } = [".mp3", ".flac", ".aac", ".ogg", ".wav", ".m4a", ".wma", ".opus"];

    /// <summary>
    /// Friendly name of the audio output device to use for music playback.
    /// Empty string means use the Windows default output device.
    /// </summary>
    public string MusicAudioDeviceId { get; init; } = string.Empty;
    public int LibraryPageSize { get; init; } = 200;

    /// <summary>
    /// Path to the folder containing updated application files.
    /// Leave empty to disable auto-update.
    /// </summary>
    public string UpdateSourcePath { get; init; } = string.Empty;

    /// <summary>
    /// How often (in minutes) to check for updates while the app is running.
    /// Set to 0 to only check at startup.
    /// </summary>
    public int AutoUpdateIntervalMinutes { get; init; } = 60;

    /// <summary>
    /// Credentials for UNC network shares that require authentication.
    /// Each entry's <see cref="NetworkShareCredential.Path"/> is matched as a prefix
    /// against the paths being scanned (e.g. <c>\\\\server\\share</c>).
    /// Credentials are stored in plain text — keep the config file private.
    /// </summary>
    public NetworkShareCredential[] NetworkShareCredentials { get; init; } = [];

    /// <summary>
    /// Additional movie library roots scanned alongside <see cref="MoviesPath"/>.
    /// Accepts absolute paths or paths relative to the application directory.
    /// </summary>
    public string[] AdditionalMoviesPaths { get; init; } = [];

    /// <summary>
    /// Additional music library roots scanned alongside <see cref="MusicPath"/>.
    /// Accepts absolute paths or paths relative to the application directory.
    /// </summary>
    public string[] AdditionalMusicPaths { get; init; } = [];

    /// <summary>
    /// Maps a video file extension (e.g. ".mkv") to an optional VLC/FFmpeg decoder hint.
    /// The hints are surfaced on the health page and can help diagnose codec failures.
    /// Example: { ".mkv": "h264", ".ts": "mpeg2video" }
    /// </summary>
    public Dictionary<string, string> VideoCodecHints { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum number of HTTP requests accepted from a single IP address within
    /// <see cref="RateLimitWindowSeconds"/>. Set to 0 to disable rate limiting.
    /// </summary>
    public int MaxRequestsPerIpPerWindow { get; init; } = 300;

    /// <summary>Duration of the per-IP rate-limit sliding window in seconds.</summary>
    public int RateLimitWindowSeconds { get; init; } = 10;

    /// <summary>
    /// How often (in hours) to automatically sync data to peer instances.
    /// 0 = never (manual only). Typical values: 4, 12, 24, 168.
    /// </summary>
    public int SyncIntervalHours { get; init; } = 0;

    /// <summary>When true, a sync-to-peers run is triggered automatically at application startup.</summary>
    public bool SyncAtStartup { get; init; }

    /// <summary>Last visited folder in the Links tab left (video) browser.</summary>
    public string LinkBrowserLeftDir { get; init; } = string.Empty;

    /// <summary>Last visited folder in the Links tab right (link) browser.</summary>
    public string LinkBrowserRightDir { get; init; } = string.Empty;

    // ── Window geometry ──────────────────────────────────────────────
    public double WindowWidth  { get; init; } = 900;
    public double WindowHeight { get; init; } = 620;
    public double WindowLeft   { get; init; } = double.NaN;
    public double WindowTop    { get; init; } = double.NaN;
    public bool   WindowMaximized { get; init; }

    // ── Browser column widths (Name / Type / Target) ─────────────────
    public double BrowserColNameWidth   { get; init; } = 220;
    public double BrowserColTypeWidth   { get; init; } = 60;
    public double BrowserColTargetWidth { get; init; } = 260;

    /// <summary>Returns all video library roots with relative paths resolved against the exe directory.</summary>
    public string[] AllResolvedMoviesPaths =>
        AdditionalMoviesPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Returns all music library roots with relative paths resolved against the exe directory.</summary>
    public string[] AllResolvedMusicPaths =>
        AdditionalMusicPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Returns the first resolved video path (for backward-compat code that reads a single primary path). Empty string if no paths are configured.</summary>
    public string ResolvedMoviesPath => AllResolvedMoviesPaths.FirstOrDefault() ?? string.Empty;

    /// <summary>Returns the first resolved music path (for backward-compat code that reads a single primary path). Empty string if no paths are configured.</summary>
    public string ResolvedMusicPath => AllResolvedMusicPaths.FirstOrDefault() ?? string.Empty;

    public string Scheme => UseHttps ? "https" : "http";

    public int EffectiveLibraryPageSize => Math.Clamp(LibraryPageSize, 25, 1000);

    public string[] EffectiveVideoFileExtensions => NormalizeExtensions(VideoFileExtensions, () => new AppConfig().VideoFileExtensions).ToArray();

    public string[] EffectiveMusicFileExtensions => NormalizeExtensions(MusicFileExtensions, () => new AppConfig().MusicFileExtensions).ToArray();

    public string[] EffectiveIgnoredLibraryFolders => NormalizeNames(IgnoredLibraryFolders).ToArray();

    /// <summary>Returns a new <see cref="AppConfig"/> identical to <paramref name="source"/> except with updated browser directory paths.</summary>
    public static AppConfig WithBrowserDirs(AppConfig source, string leftDir, string rightDir)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source with { LinkBrowserLeftDir = leftDir, LinkBrowserRightDir = rightDir };
    }

    /// <summary>Returns a new <see cref="AppConfig"/> identical to <paramref name="source"/> except with updated window geometry and browser column widths.</summary>
    public static AppConfig WithWindowLayout(AppConfig source,
        double width, double height, double left, double top, bool maximized,
        double colName, double colType, double colTarget)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source with
        {
            WindowWidth       = width,
            WindowHeight      = height,
            WindowLeft        = left,
            WindowTop         = top,
            WindowMaximized   = maximized,
            BrowserColNameWidth   = colName,
            BrowserColTypeWidth   = colType,
            BrowserColTargetWidth = colTarget,
        };
    }

    private static IEnumerable<string> NormalizeExtensions(IEnumerable<string>? values, Func<string[]> getDefaults)
    {
        var extensions = (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Select(value => value.StartsWith('.') ? value : "." + value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return extensions.Length > 0 ? extensions : getDefaults();
    }

    private static IEnumerable<string> NormalizeNames(IEnumerable<string>? values)
    {
        var names = (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Length > 0 ? names : new AppConfig().IgnoredLibraryFolders;
    }
}
