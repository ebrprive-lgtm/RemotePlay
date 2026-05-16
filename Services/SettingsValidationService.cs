using System.IO;
using RemotePlay.Abstractions.Services;
using RemotePlay.Models;

namespace RemotePlay.Services;

internal sealed class SettingsValidationService : ISettingsValidationService
{
    private const string FolderError = "⚠️ Folder does not exist. Please choose a valid path.";
    private const string PortError = "⚠️ Port must be a number between 1024 and 65535.";
    private const string FolderAccessError = "⚠️ Folder cannot be read. Please choose a folder RemotePlay can access.";

    public SettingsValidationResult Validate(string? folderPath, string? portText)
    {
        var folder = folderPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return SettingsValidationResult.Failure(FolderError);

        if (!CanReadFolder(folder))
            return SettingsValidationResult.Failure(FolderAccessError);

        var portRaw = portText?.Trim() ?? string.Empty;
        if (!int.TryParse(portRaw, out var port) || port < 1024 || port > 65535)
            return SettingsValidationResult.Failure(PortError);

        return SettingsValidationResult.Success(port);
    }

    private static bool CanReadFolder(string folder)
    {
        try
        {
            Directory.EnumerateFileSystemEntries(folder).Take(1).ToArray();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
