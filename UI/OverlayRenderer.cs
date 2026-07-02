using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.Versioning;
using WordLinkSolver.Models;

namespace WordLinkSolver.UI;

public enum OverlayStatus
{
    None,
    Scanning,
    Calculating,
}

/// <summary>Immutable-ish snapshot of everything the overlay should show this frame.</summary>
public sealed class OverlayFrame
{
    public required Size CanvasSize { get; init; }
    public required AppSettings Settings { get; init; }

    /// <summary>16 cell boxes in canvas coordinates (null before the first scan).</summary>
    public RectangleF[]? CellRects { get; init; }

    public LetterTile[]? Tiles { get; init; }

    /// <summary>The word to display, with its path.</summary>
    public WordResult? Word { get; init; }

    public bool Paused { get; init; }
    public OverlayStatus Status { get; init; }

    /// <summary>Standalone message ("No words found", startup hints, errors).</summary>
    public string? Message { get; init; }

    /// <summary>Rotation angle for the busy spinner.</summary>
    public float SpinnerAngle { get; init; }
}

/// <summary>
/// Draws overlay frames into a 32bpp ARGB bitmap (pushed to the layered
/// overlay window). Double-buffering is inherent: everything is composed
/// off-screen and swapped in one UpdateLayeredWindow call, so there is no
/// flicker by construction.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OverlayRenderer : IDisposable
{
    private const float LineWidth = 6f;
    private const float NodeRadius = 14f;

    private static readonly Color PanelBack = Color.FromArgb(225, 16, 16, 24);
    private static readonly Color AccentPurple = Color.FromArgb(255, 123, 97, 255);
    private static readonly Color PausedPurple = Color.FromArgb(255, 167, 139, 250);
    private static readonly Color UncertainRed = Color.FromArgb(230, 235, 60, 70);

    private Bitmap? _canvas;

    /// <summary>Renders a frame. The returned bitmap is owned by the renderer and reused.</summary>
    public Bitmap Render(OverlayFrame frame)
    {
        var size = new Size(Math.Max(frame.CanvasSize.Width, 1), Math.Max(frame.CanvasSize.Height, 1));
        if (_canvas is null || _canvas.Size != size)
        {
            _canvas?.Dispose();
            _canvas = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }

        using var g = Graphics.FromImage(_canvas);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Grayscale AA blends correctly against a transparent background (ClearType does not).
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        var board = BoardArea(frame);

        if (frame.Tiles is not null)
            DrawUncertainCells(g, frame.Tiles);

        if (frame.Word is not null && frame.Tiles is not null)
            DrawPath(g, frame);

        if (frame.Paused)
        {
            DrawPausedBox(g, board);
        }
        else if (frame.Status is OverlayStatus.Scanning or OverlayStatus.Calculating)
        {
            DrawBusyBox(g, board, frame.Status == OverlayStatus.Scanning ? "scanning..." : "calculating...", frame.SpinnerAngle);
        }
        else if (frame.Message is not null)
        {
            DrawMessageBox(g, board, frame.Message);
        }
        else if (frame.Word is not null && frame.Settings.ShowWordText)
        {
            DrawWordLabel(g, board, frame);
        }

        return _canvas;
    }

    private static RectangleF BoardArea(OverlayFrame frame)
    {
        if (frame.CellRects is { Length: > 0 } cells)
        {
            var union = cells[0];
            foreach (var c in cells)
                union = RectangleF.Union(union, c);
            return union;
        }
        return new RectangleF(
            frame.CanvasSize.Width * 0.05f,
            frame.CanvasSize.Height * 0.05f,
            frame.CanvasSize.Width * 0.9f,
            frame.CanvasSize.Height * 0.9f);
    }

    // ------------------------------------------------------------------
    // Swipe path
    // ------------------------------------------------------------------

    private static void DrawPath(Graphics g, OverlayFrame frame)
    {
        var word = frame.Word!;
        var tiles = frame.Tiles!;
        var start = ColorUtil.Parse(frame.Settings.LineGradientStart, Color.FromArgb(0, 255, 176));
        var end = ColorUtil.Parse(frame.Settings.LineGradientEnd, Color.FromArgb(123, 97, 255));

        int n = word.Path.Count;
        if (n < 2)
            return;

        var points = new PointF[n];
        for (int i = 0; i < n; i++)
            points[i] = tiles[word.Path[i]].PixelCenter;

        // Soft dark outline for contrast against the busy game art.
        using (var shadow = new Pen(Color.FromArgb(90, 0, 0, 0), LineWidth + 5f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLines(shadow, points);
        }

        // Gradient segments: each interpolates its share of start→end.
        for (int i = 0; i < n - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (Distance(a, b) < 0.75f)
                continue;
            var ca = ColorUtil.Lerp(start, end, i / (float)(n - 1));
            var cb = ColorUtil.Lerp(start, end, (i + 1) / (float)(n - 1));
            using var brush = new LinearGradientBrush(a, b, ca, cb);
            using var pen = new Pen(brush, LineWidth)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLine(pen, a, b);
        }

        // Bright core highlight.
        using (var core = new Pen(Color.FromArgb(80, 255, 255, 255), 2f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLines(core, points);
        }

        // Numbered order circles.
        using var numberFont = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < n; i++)
        {
            var c = ColorUtil.Lerp(start, end, n == 1 ? 0f : i / (float)(n - 1));
            var rect = new RectangleF(points[i].X - NodeRadius, points[i].Y - NodeRadius, NodeRadius * 2, NodeRadius * 2);

            using (var fill = new SolidBrush(c))
                g.FillEllipse(fill, rect);
            using (var ring = new Pen(Color.FromArgb(235, 255, 255, 255), 2f))
                g.DrawEllipse(ring, rect);

            // Auto-contrasting number text.
            double luminance = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            using var text = new SolidBrush(luminance > 145 ? Color.FromArgb(16, 16, 24) : Color.White);
            g.DrawString((i + 1).ToString(), numberFont, text, rect, format);
        }
    }

    private static float Distance(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ------------------------------------------------------------------
    // Word label + status boxes
    // ------------------------------------------------------------------

    private static float WordFontSize(WordSize size) => size switch
    {
        WordSize.Small => 17f,
        WordSize.Large => 30f,
        _ => 23f,
    };

    private static void DrawWordLabel(Graphics g, RectangleF board, OverlayFrame frame)
    {
        var word = frame.Word!;
        var textColor = ColorUtil.Parse(frame.Settings.WordTextColor, Color.FromArgb(0, 255, 176));
        string text = $"{word.Word}  •  {word.Score}";

        using var font = new Font("Segoe UI", WordFontSize(frame.Settings.WordSize), FontStyle.Bold, GraphicsUnit.Point);
        var measured = g.MeasureString(text, font);
        float padX = 18f, padY = 9f;
        float w = measured.Width + padX * 2;
        float h = measured.Height + padY * 2;

        float x = board.X + (board.Width - w) / 2f;
        float y = frame.Settings.WordPosition switch
        {
            WordPosition.Top => board.Y + board.Height * 0.06f,
            WordPosition.Bottom => board.Bottom - board.Height * 0.06f - h,
            _ => board.Y + (board.Height - h) / 2f,
        };
        var rect = new RectangleF(x, y, w, h);

        if (frame.Settings.ShowTextBackground)
        {
            using var path = RoundedRect(rect, 8f);
            using var back = new SolidBrush(PanelBack);
            g.FillPath(back, path);
            using var border = new Pen(AccentPurple, 2f);
            g.DrawPath(border, path);
        }

        using var brush = new SolidBrush(textColor);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, rect, format);
    }

    private static void DrawPausedBox(Graphics g, RectangleF board)
    {
        using var font = new Font("Segoe UI", 26f, FontStyle.Bold, GraphicsUnit.Point);
        const string text = "PAUSED";
        var measured = g.MeasureString(text, font);

        float iconSize = measured.Height * 0.72f;
        float gap = 14f;
        float padX = 26f, padY = 14f;
        float w = iconSize + gap + measured.Width + padX * 2;
        float h = measured.Height + padY * 2;
        float x = board.X + (board.Width - w) / 2f;
        float y = board.Y + (board.Height - h) / 2f;
        var rect = new RectangleF(x, y, w, h);

        // Double purple frame, like the reference screenshot.
        var outer = RectangleF.Inflate(rect, 8f, 8f);
        using (var outerPath = RoundedRect(outer, 6f))
        using (var back = new SolidBrush(PanelBack))
        using (var pen = new Pen(AccentPurple, 2f))
        {
            g.FillPath(back, outerPath);
            g.DrawPath(pen, outerPath);
        }
        using (var innerPath = RoundedRect(rect, 4f))
        using (var pen = new Pen(Color.FromArgb(150, AccentPurple), 1.5f))
        {
            g.DrawPath(pen, innerPath);
        }

        // ⏸ icon: rounded square outline with two bars.
        var iconRect = new RectangleF(rect.X + padX, rect.Y + (h - iconSize) / 2f, iconSize, iconSize);
        using (var iconPath = RoundedRect(iconRect, iconSize * 0.22f))
        using (var pen = new Pen(PausedPurple, 2.5f))
        {
            g.DrawPath(pen, iconPath);
        }
        using (var bar = new SolidBrush(PausedPurple))
        {
            float barW = iconSize * 0.16f;
            float barH = iconSize * 0.46f;
            float byy = iconRect.Y + (iconSize - barH) / 2f;
            g.FillRectangle(bar, iconRect.X + iconSize * 0.28f, byy, barW, barH);
            g.FillRectangle(bar, iconRect.Right - iconSize * 0.28f - barW, byy, barW, barH);
        }

        using var brush = new SolidBrush(PausedPurple);
        using var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush,
            new RectangleF(iconRect.Right + gap, rect.Y, measured.Width + padX, h), format);
    }

    private static void DrawBusyBox(Graphics g, RectangleF board, string text, float spinnerAngle)
    {
        using var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Point);
        var measured = g.MeasureString(text, font);

        float spinner = 30f;
        float padX = 40f, padY = 16f;
        float w = Math.Max(measured.Width, spinner) + padX * 2;
        float h = spinner + 8f + measured.Height + padY * 2;
        float x = board.X + (board.Width - w) / 2f;
        float y = board.Y + (board.Height - h) / 2f;
        var rect = new RectangleF(x, y, w, h);

        using (var path = RoundedRect(rect, 6f))
        using (var back = new SolidBrush(PanelBack))
        using (var pen = new Pen(Color.FromArgb(170, AccentPurple), 1.5f))
        {
            g.FillPath(back, path);
            g.DrawPath(pen, path);
        }

        var spinnerRect = new RectangleF(rect.X + (w - spinner) / 2f, rect.Y + padY, spinner, spinner);
        using (var pen = new Pen(AccentPurple, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(pen, spinnerRect, spinnerAngle, 285f);
        }

        using var brush = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush,
            new RectangleF(rect.X, spinnerRect.Bottom + 4f, w, measured.Height + 4f), format);
    }

    private static void DrawMessageBox(Graphics g, RectangleF board, string message)
    {
        using var font = new Font("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Point);
        var maxWidth = Math.Max(board.Width * 0.85f, 200f);
        var measured = g.MeasureString(message, font, (int)maxWidth);
        float padX = 24f, padY = 14f;
        float w = measured.Width + padX * 2;
        float h = measured.Height + padY * 2;
        float x = board.X + (board.Width - w) / 2f;
        float y = board.Y + (board.Height - h) / 2f;
        var rect = new RectangleF(x, y, w, h);

        using (var path = RoundedRect(rect, 6f))
        using (var back = new SolidBrush(PanelBack))
        using (var pen = new Pen(Color.FromArgb(170, AccentPurple), 1.5f))
        {
            g.FillPath(back, path);
            g.DrawPath(pen, path);
        }

        using var brush = new SolidBrush(Color.FromArgb(235, 235, 240, 255));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(message, font, brush, rect, format);
    }

    private static void DrawUncertainCells(Graphics g, LetterTile[] tiles)
    {
        using var pen = new Pen(UncertainRed, 2.5f);
        using var font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(UncertainRed);
        foreach (var tile in tiles)
        {
            if (!tile.IsUncertain || tile.CellBounds.IsEmpty)
                continue;
            var r = RectangleF.Inflate(tile.CellBounds, -2f, -2f);
            using var path = RoundedRect(r, r.Width * 0.18f);
            g.DrawPath(pen, path);
            g.DrawString("?", font, brush, r.X + 4, r.Y + 2);
        }
    }

    internal static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _canvas?.Dispose();
        _canvas = null;
    }
}
