using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WordLinkSolver.Core;

/// <summary>
/// BitBlt-based region screen capture — measurably faster than
/// Graphics.CopyFromScreen for small regions and it lets us capture straight
/// into a reusable bitmap.
///
/// The app's own windows are excluded from capture via
/// SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE), so the overlay never
/// pollutes its own OCR input.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenCapture
{
    private const uint SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    /// <summary>
    /// Hides a window from screen capture (Windows 10 2004+). Returns false on
    /// older systems, where the caller should briefly hide the overlay while
    /// capturing instead.
    /// </summary>
    public static bool TryExcludeFromCapture(IntPtr hwnd) =>
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

    /// <summary>
    /// Captures a screen rectangle (physical pixels) into a 32bpp bitmap.
    /// Target: &lt;5ms for a ~500×500 region.
    /// </summary>
    public static Bitmap Capture(Rectangle screenRect)
    {
        var bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr hdcDest = g.GetHdc();
        IntPtr hdcSrc = GetDC(IntPtr.Zero);
        try
        {
            BitBlt(hdcDest, 0, 0, screenRect.Width, screenRect.Height,
                   hdcSrc, screenRect.X, screenRect.Y, SRCCOPY);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdcSrc);
            g.ReleaseHdc(hdcDest);
        }
        return bmp;
    }

    /// <summary>
    /// Copies a bitmap's pixels into a tightly packed BGRA byte array for the
    /// pure-math pipeline (grid detection, hashing).
    /// </summary>
    public static byte[] GetPixels(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bmp.Width * 4;
            var pixels = new byte[rowBytes * bmp.Height];
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            else
            {
                for (int y = 0; y < bmp.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);
            }
            return pixels;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
