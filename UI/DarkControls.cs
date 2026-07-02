using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;

namespace WordLinkSolver.UI;

/// <summary>Shared dark theme palette (matches the reference design).</summary>
[SupportedOSPlatform("windows")]
public static class Theme
{
    public static readonly Color WindowBack = Color.FromArgb(15, 15, 23);      // #0F0F17
    public static readonly Color CardBack = Color.FromArgb(23, 23, 32);        // #171720
    public static readonly Color ControlBack = Color.FromArgb(38, 38, 51);     // #262633
    public static readonly Color ControlHover = Color.FromArgb(50, 50, 66);
    public static readonly Color Divider = Color.FromArgb(35, 35, 46);
    public static readonly Color Text = Color.FromArgb(236, 236, 244);
    public static readonly Color TextMuted = Color.FromArgb(138, 138, 160);
    public static readonly Color Accent = Color.FromArgb(123, 97, 255);        // #7B61FF
    public static readonly Color AccentHover = Color.FromArgb(143, 121, 255);
    public static readonly Color CloseRed = Color.FromArgb(200, 30, 60);
    public static readonly Color ChipBack = Color.FromArgb(24, 24, 33);
}

/// <summary>An iOS-style on/off switch (purple when on), as in the reference settings UI.</summary>
[SupportedOSPlatform("windows")]
public sealed class ToggleSwitch : Control
{
    private bool _checked;
    private bool _hover;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            CheckedChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(52, 26);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var track = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        var back = _checked
            ? (_hover ? Theme.AccentHover : Theme.Accent)
            : (_hover ? Theme.ControlHover : Theme.ControlBack);

        using (var path = OverlayRenderer.RoundedRect(track, Height / 2f))
        using (var brush = new SolidBrush(back))
        {
            g.FillPath(brush, path);
        }

        float knob = Height - 6f;
        float x = _checked ? Width - knob - 3f : 3f;
        using (var brush = new SolidBrush(Color.White))
            g.FillEllipse(brush, x, 3f, knob, knob);
    }
}

/// <summary>Rounded pill button (purple accent or neutral dark).</summary>
[SupportedOSPlatform("windows")]
public sealed class PillButton : Button
{
    public bool IsAccent { get; set; }

    private bool _hover;

    public PillButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        Size = new Size(104, 40);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.WindowBack);

        var fill = IsAccent
            ? (_hover ? Theme.AccentHover : Theme.Accent)
            : (_hover ? Theme.ControlHover : Theme.ControlBack);

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = OverlayRenderer.RoundedRect(rect, Height / 2f))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>A flat color swatch that opens a color picker when clicked.</summary>
[SupportedOSPlatform("windows")]
public sealed class ColorSwatch : Control
{
    public Color Selected { get; set; } = Color.White;

    public event EventHandler? ColorChanged;

    public ColorSwatch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(46, 46);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        using var dialog = new ColorDialog { Color = Selected, FullOpen = true };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            Selected = dialog.Color;
            ColorChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.CardBack);
        using var brush = new SolidBrush(Selected);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        g.FillRectangle(brush, rect);
        using var pen = new Pen(Color.FromArgb(70, 255, 255, 255));
        g.DrawRectangle(pen, rect);
    }
}
