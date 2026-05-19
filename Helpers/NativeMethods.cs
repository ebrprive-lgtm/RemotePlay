using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;

namespace RemotePlay.Helpers;

[ExcludeFromCodeCoverage]
internal static class NativeMethods
{
    private const int SWP_NOZORDER      = 0x0004;
    private const int SWP_NOACTIVATE    = 0x0010;
    private const int SWP_FRAMECHANGED  = 0x0020;

    /// <summary>
    /// Moves and resizes a window in one atomic call using physical pixel coordinates.
    /// Using SetWindowPos avoids the intermediate WPF DPI-change rescaling that occurs
    /// when Left/Top/Width/Height are set individually, which would cause a momentary
    /// oversized flash before the watchdog corrects the bounds.
    /// </summary>
    internal static void SetWindowBoundsPhysical(nint hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, nint.Zero, x, y, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy,
        uint uFlags);
}
