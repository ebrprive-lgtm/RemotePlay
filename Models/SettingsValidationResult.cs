namespace RemotePlay.Models;

internal sealed record SettingsValidationResult
{
    public bool IsValid { get; init; }
    public int? ParsedPort { get; init; }
    public string? ErrorMessage { get; init; }

    public static SettingsValidationResult Success(int port) =>
        new() { IsValid = true, ParsedPort = port };

    public static SettingsValidationResult Failure(string message) =>
        new() { IsValid = false, ErrorMessage = message };
}
