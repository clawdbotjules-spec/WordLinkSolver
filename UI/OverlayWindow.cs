using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WordLinkSolver.Core;

namespace WordLinkSolver.UI;

/// <summary>
/// The transparent, click-through, always-on-top overlay surface.
///
/// It is a layered window (per-pixel alpha via UpdateLayeredWindow) with
/// WS_EX_TRANSPARENT so mouse input goes straight through to the game, and it
/// is owned by the main window so it always floats directly above it. It is
/// also excluded from screen capture so the solver never OCRs its own drawing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OverlayWindow : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    /// <summary>True when the OS honors exclude-from-capture for this window.</summary>
    public bool ExcludedFromCapture { get; private set; }

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Bounds = new Rectangle(0, 0, 1, 1);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExcludedFromCapture = ScreenCapture.TryExcludeFromCapture(Handle);
    }

    /// <summary>Moves the overlay to the given screen rectangle without activating it.</summary>
    public void SyncBounds(Rectangle screenRect)
    {
        if (!IsHandleCreated)
            return;
        SetWindowPos(Handle, IntPtr.Zero,
            screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height,
            SWP_NOACTIVATE | SWP_NOZORDER);
    }

    /// <summary>
    /// Pushes a fully composed ARGB frame to the screen in a single atomic
    /// swap (no flicker; the bitmap stays owned by the caller).
    /// </summary>
    public void PushFrame(Bitmap frame)
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = frame.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = frame.Width, cy = frame.Height };
            var source = new POINT { x = 0, y = 0 };
            var topLeft = new POINT { x = Left, y = Top };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            UpdateLayeredWindow(Handle, screenDc, ref topLeft, ref size,
                memDc, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
                SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
