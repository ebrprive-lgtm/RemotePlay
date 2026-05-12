namespace RemotePlay.Models;

internal sealed record DisplayDiagnostics
{
    public bool IsVideoMode { get; init; }
    public int PreferredDisplayIndex { get; init; }
    public int TargetDisplayIndex { get; init; }
    public string TargetDisplayName { get; init; } = string.Empty;
    public int TargetLeft { get; init; }
    public int TargetTop { get; init; }
    public int TargetWidth { get; init; }
    public int TargetHeight { get; init; }
    public string WindowState { get; init; } = string.Empty;
    public string WindowStyle { get; init; } = string.Empty;
    public string ResizeMode { get; init; } = string.Empty;
    public bool Topmost { get; init; }
    public int WindowLeft { get; init; }
    public int WindowTop { get; init; }
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }
    public string CurrentFilePath { get; init; } = string.Empty;
    public string CurrentTitle { get; init; } = string.Empty;
    public double Zoom { get; init; } = 1;
    public double Brightness { get; init; } = 0.5;
    public double Saturation { get; init; } = 1;
    public double VideoSurfaceWidth { get; init; }
    public double VideoSurfaceHeight { get; init; }
    public double VideoPlayerActualWidth { get; init; }
    public double VideoPlayerActualHeight { get; init; }
    public double DpiScaleX { get; init; }
    public double DpiScaleY { get; init; }
    public bool NeedsFullscreenRepair { get; init; }
    public DisplayInfo[] Displays { get; init; } = [];
}
