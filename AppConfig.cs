using System.IO;
using System.Text.Json;

namespace RemotePlay;

internal sealed class AppConfig
{
    public int Port { get; init; } = 9090;
    public string MoviesPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "Movies");

    private static readonly string ConfigFile =
        Path.Combine(AppContext.BaseDirectory, "remoteplay.json");

    /// <summary>Returns the fully-resolved movies path (relative paths are resolved against the exe directory).</summary>
    public string ResolvedMoviesPath =>
        Path.IsPathRooted(MoviesPath)
            ? MoviesPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, MoviesPath));

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
                    Logger.Info($"Config loaded — Port: {config.Port}, MoviesPath: {config.ResolvedMoviesPath}");
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
