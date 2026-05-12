using RemotePlay.Abstractions.Services;

namespace RemotePlay.Services;

internal sealed class AppConfigService : IAppConfigService
{
    public AppConfig Load() => AppConfig.Load();

    public void Save(AppConfig config) => AppConfig.Save(config);
}
