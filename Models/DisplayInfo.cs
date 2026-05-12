namespace RemotePlay.Models;

internal sealed record DisplayInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int WorkingLeft { get; init; }
    public int WorkingTop { get; init; }
    public int WorkingWidth { get; init; }
    public int WorkingHeight { get; init; }
}
