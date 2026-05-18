namespace RemotePlay;

/// <summary>Represents a <c>.rplink</c> file displayed in the Library Links list.</summary>
internal sealed record LinkItem(
    string LinkName,
    string FolderName,
    string TargetPath,
    string RplinkPath);
