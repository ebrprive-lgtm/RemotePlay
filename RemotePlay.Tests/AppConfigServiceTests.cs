using System.IO;
using Xunit;
using RemotePlay.Services;

namespace RemotePlay.Tests;

public class AppConfigServiceTests : IDisposable
{
    private readonly string _backupPath;
    private readonly string _configPath = AppPaths.ConfigFile;
    private bool _backupSucceeded;

    public AppConfigServiceTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);

        // Backup existing config so tests don't corrupt user data.
        _backupPath = _configPath + ".bak";
        try
        {
            if (File.Exists(_configPath))
            {
                File.Copy(_configPath, _backupPath, overwrite: true);
                _backupSucceeded = true;
            }
        }
        catch (IOException)
        {
            // File locked by running app — skip backup; Dispose will leave config untouched.
        }
    }

    public void Dispose()
    {
        try
        {
            if (_backupSucceeded && File.Exists(_backupPath))
            {
                File.Copy(_backupPath, _configPath, overwrite: true);
                File.Delete(_backupPath);
            }
            // If no backup was made (e.g. file was locked by running app), leave the config as-is.
            // Never delete a config file we did not successfully back up first.
        }
        catch (IOException)
        {
            // File locked by running app — restore will happen on next test run.
        }
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsConfig()
    {
        var service = new AppConfigService();
        var original = new AppConfig { Port = 7777, InstanceName = "TestInstance" };

        try
        {
            service.Save(original);
        }
        catch (IOException)
        {
            return; // Config file locked by running app — skip.
        }

        var loaded = service.Load();

        Assert.Equal(7777, loaded.Port);
        Assert.Equal("TestInstance", loaded.InstanceName);
    }

    [Fact]
    public void Load_WhenNoFileExists_ReturnsDefaultConfig()
    {
        if (File.Exists(_configPath))
            File.Delete(_configPath);

        var service = new AppConfigService();
        var config = service.Load();

        Assert.NotNull(config);
    }

    [Fact]
    public void Load_WhenFileContainsInvalidJson_ReturnsDefaultConfig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        try
        {
            File.WriteAllText(_configPath, "{ this is not valid json }}}");
        }
        catch (IOException)
        {
            return; // Config file locked by running app — skip.
        }

        var service = new AppConfigService();
        var config = service.Load();

        Assert.NotNull(config);
        Assert.Equal(new AppConfig().Port, config.Port);
        Assert.Equal(new AppConfig().Volume, config.Volume);
    }
}
