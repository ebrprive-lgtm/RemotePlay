using System.IO;
using System.Text.Json;

namespace RemotePlay;

internal sealed class AppConfig
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
    public bool RescanLibraryOnStartup { get; init; }
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
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFile, json);
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                var config = JsonSerializer.Deserialize<AppConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
