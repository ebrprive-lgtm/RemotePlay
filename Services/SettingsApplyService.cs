using RemotePlay.Abstractions.Services;

namespace RemotePlay.Services;

internal sealed class SettingsApplyService : ISettingsApplyService
{
    private readonly IAppConfigService _appConfigService;

    public SettingsApplyService(IAppConfigService appConfigService)
    {
        _appConfigService = appConfigService;
    }

    public AppConfig ApplyAndReload(AppConfig updatedConfig)
    {
        ArgumentNullException.ThrowIfNull(updatedConfig);

        _appConfigService.Save(updatedConfig);
        return _appConfigService.Load();
    }
}
