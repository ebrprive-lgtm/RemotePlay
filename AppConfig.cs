using System.IO;
using System.Text.Json;

namespace RemotePlay;

internal sealed class AppConfig
{
    public int Port { get; init; } = 9090;
    public bool UseHttps { get; init; }
    public string MoviesPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "Movies");
    public double Volume { get; init; } = 1;
    public double Brightness { get; init; }
    public double AudioBoost { get; init; } = 1;
    public double PlaybackSpeed { get; init; } = 1;
    public bool SubtitlesEnabled { get; init; } = true;

    private static readonly string ConfigFile =
        Path.Combine(AppContext.BaseDirectory, "remoteplay.json");

    /// <summary>Returns the fully-resolved movies path (relative paths are resolved against the exe directory).</summary>
    public string ResolvedMoviesPath =>
        Path.IsPathRooted(MoviesPath)
            ? MoviesPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, MoviesPath));

    public string Scheme => UseHttps ? "https" : "http";

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
}
