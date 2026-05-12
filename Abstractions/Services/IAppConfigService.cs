namespace RemotePlay.Abstractions.Services;

internal interface IAppConfigService
{
    AppConfig Load();
    void Save(AppConfig config);
}
