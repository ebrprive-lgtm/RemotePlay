using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemotePlay.Abstractions.Services;

namespace RemotePlay.Services;

internal sealed class AppConfigService : IAppConfigService
{
    private static readonly string ConfigFile = AppPaths.ConfigFile;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                var config = JsonSerializer.Deserialize<AppConfig>(json, ReadOptions);
                if (config is not null)
                {
                    Logger.Info($"Config loaded — Scheme: {config.Scheme}, Port: {config.Port}, MoviesPath: {config.ResolvedMoviesPath}, MusicPath: {config.ResolvedMusicPath}");
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

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var json = JsonSerializer.Serialize(config, WriteOptions);

        // Write to a temp file first, then atomically replace the real config file.
        // This prevents a crash mid-write from corrupting the config and silently resetting all settings.
        var tmp = ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(ConfigFile))
            File.Replace(tmp, ConfigFile, destinationBackupFileName: null);
        else
            File.Move(tmp, ConfigFile);
    }
}
