namespace RemotePlay.Helpers;

internal static class FullscreenBoundsEvaluator
{
    public static bool NeedsRepair(
        bool isVideoMode,
        string windowStyle,
        string resizeMode,
        string windowState,
        bool topmost,
        int windowLeft,
        int windowTop,
        int windowWidth,
        int windowHeight,
        int targetLeft,
        int targetTop,
        int targetWidth,
        int targetHeight,
        int tolerancePixels = 2)
    {
        if (!isVideoMode)
            return false;

        return !string.Equals(windowStyle, "None", StringComparison.Ordinal)
            || !string.Equals(resizeMode, "NoResize", StringComparison.Ordinal)
            || !string.Equals(windowState, "Maximized", StringComparison.Ordinal)
            || !topmost
            || Math.Abs(windowLeft - targetLeft) > tolerancePixels
            || Math.Abs(windowTop - targetTop) > tolerancePixels
            || Math.Abs(windowWidth - targetWidth) > tolerancePixels
            || Math.Abs(windowHeight - targetHeight) > tolerancePixels;
    }
}
