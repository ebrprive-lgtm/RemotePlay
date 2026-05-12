using RemotePlay.Models;

namespace RemotePlay.Abstractions.Services;

internal interface ISettingsValidationService
{
    SettingsValidationResult Validate(string? folderPath, string? portText);
}
