using RemotePlay;
using RemotePlay.Abstractions.Services;
using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

public sealed class SettingsApplyServiceTests
{
    [Fact]
    public void ApplyAndReloadSavesThenLoadsAndReturnsLoadedConfig()
    {
        var expected = new AppConfig { Port = 7777, MoviesPath = "C:\\Movies" };
        var fake = new FakeAppConfigService(expected);
        var service = new SettingsApplyService(fake);
        var toSave = new AppConfig { Port = 5000, MoviesPath = "D:\\Media" };

        var result = service.ApplyAndReload(toSave);

        Assert.Same(toSave, fake.SavedConfig);
        Assert.Same(expected, result);
        Assert.Equal(1, fake.SaveCalls);
        Assert.Equal(1, fake.LoadCalls);
    }

    private sealed class FakeAppConfigService : IAppConfigService
    {
        private readonly AppConfig _toLoad;

        public FakeAppConfigService(AppConfig toLoad)
        {
            _toLoad = toLoad;
        }

        public int SaveCalls { get; private set; }
        public int LoadCalls { get; private set; }
        public AppConfig? SavedConfig { get; private set; }

        public AppConfig Load()
        {
            LoadCalls++;
            return _toLoad;
        }

        public void Save(AppConfig config)
        {
            SaveCalls++;
            SavedConfig = config;
        }
    }
}
