namespace RemotePlay.Abstractions.Services;

internal interface ISettingsApplyService
{
    AppConfig ApplyAndReload(AppConfig updatedConfig);
}
