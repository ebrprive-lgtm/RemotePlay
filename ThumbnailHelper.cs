using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Media.Imaging;

namespace RemotePlay;

/// <summary>
/// Extracts video thumbnails using the Windows Shell thumbnail cache
/// (the same images Explorer shows). No external tools required.
/// </summary>
internal static class ThumbnailHelper
{
    // ── Shell COM interfaces ──────────────────────────────────────────────────

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
        void BindToHandler(IBindCtx pbc, ref Guid bhid, ref Guid riid, out nint ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out nint ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out nint phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE(int cx, int cy)
    {
        public int cx = cx;
        public int cy = cy;
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit      = 0x00,
        BiggerSizeOk     = 0x01,
        MemoryOnly       = 0x02,
        IconOnly         = 0x04,
        ThumbnailOnly    = 0x08,
        InCacheOnly      = 0x10,
        CropToSquare     = 0x20,
        WideThumbnails   = 0x40,
        IconBackground   = 0x80,
        ScaleUp          = 0x100,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IBindCtx? pbc, ref Guid riid, out IShellItem ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns JPEG bytes of the thumbnail for <paramref name="filePath"/>,
    /// or <c>null</c> if extraction fails (e.g. no cached thumbnail yet).
    /// </summary>
    public static byte[]? GetJpegThumbnail(string filePath, int size = 320)
    {
        try
        {
            var riid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(filePath, null, ref riid, out var shellItem);

            if (shellItem is not IShellItemImageFactory factory)
                return null;

            int hr = factory.GetImage(new SIZE(size, size), SIIGBF.ResizeToFit, out nint hBitmap);
            if (hr != 0 || hBitmap == 0)
                return null;

            try
            {
                // Convert GDI HBITMAP to WPF BitmapSource, then encode as JPEG
                var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    nint.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                using var ms = new MemoryStream();
                var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                encoder.Save(ms);
                return ms.ToArray();
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Thumbnail extraction failed for {filePath}", ex);
            return null;
        }
    }
}
