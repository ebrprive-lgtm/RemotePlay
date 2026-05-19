using System.IO;
using System.Text.Json;

namespace RemotePlay;

internal sealed record AppConfig
{
    public int Port { get; init; } = 9090;
    public bool UseHttps { get; init; }
    /// <summary>Stable identifier for this instance. Used to deduplicate peer entries when a machine has multiple network interfaces.</summary>
    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Friendly display name shown in the peer discovery list. Defaults to the machine hostname.</summary>
    public string InstanceName { get; init; } = System.Net.Dns.GetHostName();
    public string MoviesPath { get; init; } = Path.Combine(AppPaths.UserDataDirectory, "Movies");
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
    public int LibraryRescanDelayMinutes { get; init; } = 10;
    public bool EnableThumbnailGeneration { get; init; } = true;
    public string[] IgnoredLibraryFolders { get; init; } = ["Subs", "Alt"];
    public string[] VideoFileExtensions { get; init; } = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv"];
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

    private static readonly string ConfigFile = AppPaths.ConfigFile;

    /// <summary>Returns the fully-resolved movies path (relative paths are resolved against the exe directory).</summary>
    public string ResolvedMoviesPath =>
        Path.IsPathRooted(MoviesPath)
            ? MoviesPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, MoviesPath));

    public string Scheme => UseHttps ? "https" : "http";

    public int EffectiveLibraryPageSize => Math.Clamp(LibraryPageSize, 25, 1000);

    public string[] EffectiveVideoFileExtensions => NormalizeExtensions(VideoFileExtensions).ToArray();

    public string[] EffectiveIgnoredLibraryFolders => NormalizeNames(IgnoredLibraryFolders).ToArray();

    public static void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var json = JsonSerializer.Serialize(config,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            });

        // Write to a temp file first, then atomically replace the real config file.
        // This prevents a crash mid-write from corrupting the config and silently resetting all settings.
        var tmp = ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(ConfigFile))
            File.Replace(tmp, ConfigFile, destinationBackupFileName: null);
        else
            File.Move(tmp, ConfigFile);
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                var config = JsonSerializer.Deserialize<AppConfig>(json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    });
                if (config is not null)
                {
                    Logger.Info($"Config loaded — Scheme: {config.Scheme}, Port: {config.Port}, MoviesPath: {config.ResolvedMoviesPath}");
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load config, using defaults", ex);
        }

        return new AppConfig();
    }

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

    private static IEnumerable<string> NormalizeExtensions(IEnumerable<string>? values)
    {
        var extensions = (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Select(value => value.StartsWith('.') ? value : "." + value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return extensions.Length > 0 ? extensions : new AppConfig().VideoFileExtensions;
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

internal enum PlaybackEndMode
{
    Stop,
    PlayNext,
    ReturnToLibrary
}
