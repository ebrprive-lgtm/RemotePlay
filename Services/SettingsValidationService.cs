using System.IO;
using RemotePlay.Abstractions.Services;
using RemotePlay.Models;

namespace RemotePlay.Services;

internal sealed class SettingsValidationService : ISettingsValidationService
{
    private const string PortError = "⚠️ Port must be a number between 1024 and 65535.";

    public SettingsValidationResult Validate(string? portText)
    {
        var portRaw = portText?.Trim() ?? string.Empty;
        if (!int.TryParse(portRaw, out var port) || port < 1024 || port > 65535)
            return SettingsValidationResult.Failure(PortError);

        return SettingsValidationResult.Success(port);
    }
}
