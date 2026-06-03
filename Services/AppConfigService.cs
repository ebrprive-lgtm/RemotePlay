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

    private static readonly string ConfigBackupFile = AppPaths.ConfigFile + ".bak";

    public AppConfig Load()
    {
        foreach (var path in new[] { ConfigFile, ConfigBackupFile })
        {
            try
            {
                if (!File.Exists(path))
                {
                    Logger.Info($"AppConfigService.Load — {Path.GetFileName(path)} not found, skipping");
                    continue;
                }

                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<AppConfig>(json, ReadOptions);
                if (config is not null)
                {
                    config = MigrateConfig(config);
                    Logger.Info($"AppConfigService.Load ✓ from {Path.GetFileName(path)} — VideoPaths={config.AdditionalMoviesPaths.Length} | MusicPaths={config.AdditionalMusicPaths.Length} | InstanceName={config.InstanceName}");
                    return config;
                }

                Logger.Info($"AppConfigService.Load — {Path.GetFileName(path)} deserialized to null, skipping");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load config from {Path.GetFileName(path)}, trying next", ex);
            }
        }

        Logger.Info("AppConfigService.Load — no valid config found, using defaults");
        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var json = JsonSerializer.Serialize(config, WriteOptions);

        Logger.Info($"AppConfigService.Save → {ConfigFile} | VideoPaths={config.AdditionalMoviesPaths.Length} | MusicPaths={config.AdditionalMusicPaths.Length} | InstanceName={config.InstanceName}");
        Logger.Info($"AppConfigService.Save caller: {new System.Diagnostics.StackTrace(1, false).ToString().Split('\n')[0].Trim()}");

        // Write to a temp file first, then atomically replace the real config file.
        // This prevents a crash mid-write from corrupting the config and silently resetting all settings.
        var tmp = ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(ConfigFile))
            File.Replace(tmp, ConfigFile, destinationBackupFileName: ConfigFile + ".bak");
        else
            File.Move(tmp, ConfigFile);

        Logger.Info($"AppConfigService.Save ✓ complete");
    }

    /// <summary>
    /// Migrates configs saved before the path-list simplification: if AdditionalMoviesPaths or
    /// AdditionalMusicPaths is empty and the legacy primary path differs from the default, promote
    /// the primary path into the array so existing settings are not lost after upgrade.
    /// </summary>
    private static AppConfig MigrateConfig(AppConfig config)
    {
        var defaults = new AppConfig();
        var videoPaths = config.AdditionalMoviesPaths;
        var musicPaths = config.AdditionalMusicPaths;

        if (videoPaths.Length == 0
            && !string.IsNullOrWhiteSpace(config.MoviesPath)
            && !string.Equals(config.MoviesPath, defaults.MoviesPath, StringComparison.OrdinalIgnoreCase))
        {
            videoPaths = [config.MoviesPath];
        }

        if (musicPaths.Length == 0
            && !string.IsNullOrWhiteSpace(config.MusicPath)
            && !string.Equals(config.MusicPath, defaults.MusicPath, StringComparison.OrdinalIgnoreCase))
        {
            musicPaths = [config.MusicPath];
        }

        if (ReferenceEquals(videoPaths, config.AdditionalMoviesPaths) && ReferenceEquals(musicPaths, config.AdditionalMusicPaths))
            return config;

        return config with { AdditionalMoviesPaths = videoPaths, AdditionalMusicPaths = musicPaths };
    }
}
