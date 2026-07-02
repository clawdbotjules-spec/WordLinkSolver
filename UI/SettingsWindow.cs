using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WordLinkSolver.Models;

namespace WordLinkSolver.UI;

/// <summary>
/// Dark-themed settings dialog (custom chrome, matching the reference design:
/// near-black background with a faint grid, purple accents, section cards,
/// color swatches, dropdowns and toggle switches).
///
/// The dialog edits a copy of the settings; <see cref="Result"/> is valid when
/// ShowDialog returns OK.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsWindow : Form
{
    private const int TitleBarHeight = 36;
    private const int RowHeight = 62;

    public AppSettings Result { get; }

    private readonly ColorSwatch _gradientStart = new();
    private readonly ColorSwatch _gradientEnd = new();
    private readonly ColorSwatch _wordColor = new();
    private readonly ComboBox _wordPosition = MakeCombo(130);
    private readonly ComboBox _wordSize = MakeCombo(130);
    private readonly ToggleSwitch _showBackground = new();
    private readonly ToggleSwitch _showWordText = new();
    private readonly ComboBox _refreshSpeed = MakeCombo(170);

    public SettingsWindow(AppSettings current)
    {
        Result = current;

        Text = "WordLink Solver - Settings";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.WindowBack;
        ForeColor = Theme.Text;
        ClientSize = new Size(470, 700);
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        KeyPreview = true;
        ShowInTaskbar = false;

        BuildChrome();
        BuildContent();
        LoadFrom(current);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                DialogResult = DialogResult.Cancel;
        };
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    private static ComboBox MakeCombo(int width)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.ControlBack,
            ForeColor = Theme.Text,
            Width = width,
            Font = new Font("Segoe UI", 10f),
        };
        return combo;
    }

    private void BuildChrome()
    {
        // Custom title bar: caption text + red close button, draggable.
        var titleBar = new Panel
        {
            Bounds = new Rectangle(0, 0, ClientSize.Width, TitleBarHeight),
            BackColor = Color.FromArgb(20, 20, 29),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        titleBar.Paint += (_, e) =>
        {
            TextRenderer.DrawText(e.Graphics, "WordLink Solver - Settings",
                new Font("Segoe UI", 9.5f), new Point(12, 9), Theme.Text);
            using var pen = new Pen(Theme.Accent, 2f);
            e.Graphics.DrawLine(pen, 0, TitleBarHeight - 1, ClientSize.Width, TitleBarHeight - 1);
        };
        titleBar.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        };

        var close = new Button
        {
            Text = "✕",
            Bounds = new Rectangle(ClientSize.Width - 34, 5, 26, 24),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.CloseRed,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TabStop = false,
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;

        titleBar.Controls.Add(close);
        Controls.Add(titleBar);
    }

    private void BuildContent()
    {
        // Header: app icon + name + subtitle.
        var header = new Panel
        {
            Bounds = new Rectangle(24, TitleBarHeight + 18, ClientSize.Width - 48, 64),
            BackColor = Color.Transparent,
        };
        header.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawAppGlyph(e.Graphics, new RectangleF(0, 4, 56, 56));
            TextRenderer.DrawText(e.Graphics, "WordLink Solver",
                new Font("Segoe UI", 17f, FontStyle.Bold), new Point(70, 8), Theme.Text);
            TextRenderer.DrawText(e.Graphics, "Settings & appearance",
                new Font("Segoe UI", 9.5f), new Point(72, 38), Theme.TextMuted);
        };
        Controls.Add(header);

        int y = TitleBarHeight + 100;

        y = AddSectionLabel("APPEARANCE", y);
        var appearance = AddCard(y, 7);
        AddRow(appearance, 0, "Line gradient start", _gradientStart);
        AddRow(appearance, 1, "Line gradient end", _gradientEnd);
        AddRow(appearance, 2, "Word text colour", _wordColor);
        AddRow(appearance, 3, "Word position", _wordPosition);
        AddRow(appearance, 4, "Word size", _wordSize);
        AddRow(appearance, 5, "Show text background", _showBackground);
        AddRow(appearance, 6, "Show word text", _showWordText);
        y = appearance.Bottom + 18;

        y = AddSectionLabel("PERFORMANCE", y);
        var performance = AddCard(y, 1);
        AddRow(performance, 0, "Refresh speed", _refreshSpeed);
        y = performance.Bottom + 22;

        _wordPosition.Items.AddRange(new object[] { "top", "middle", "bottom" });
        _wordSize.Items.AddRange(new object[] { "Small", "Medium", "Large" });
        _refreshSpeed.Items.AddRange(new object[]
        {
            "Fast (50ms)", "Normal (100ms)", "Slow (250ms)", "Manual (F key only)",
        });

        var cancel = new PillButton { Text = "Cancel" };
        cancel.Location = new Point(ClientSize.Width - 24 - 104 - 12 - 104, y);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        var save = new PillButton { Text = "Save", IsAccent = true };
        save.Location = new Point(ClientSize.Width - 24 - 104, y);
        save.Click += (_, _) => SaveAndClose();

        Controls.Add(cancel);
        Controls.Add(save);
        AcceptButton = save;

        ClientSize = new Size(ClientSize.Width, y + 40 + 20);
    }

    private int AddSectionLabel(string text, int y)
    {
        var label = new Label
        {
            Text = text,
            Bounds = new Rectangle(24, y, ClientSize.Width - 48, 20),
            ForeColor = Theme.TextMuted,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            BackColor = Color.Transparent,
        };
        Controls.Add(label);
        return y + 26;
    }

    private Panel AddCard(int y, int rowCount)
    {
        var card = new Panel
        {
            Bounds = new Rectangle(24, y, ClientSize.Width - 48, rowCount * RowHeight),
            BackColor = Theme.CardBack,
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Divider);
            for (int i = 1; i < rowCount; i++)
                e.Graphics.DrawLine(pen, 12, i * RowHeight, card.Width - 12, i * RowHeight);
        };
        Controls.Add(card);
        return card;
    }

    private static void AddRow(Panel card, int rowIndex, string labelText, Control control)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = false,
            Bounds = new Rectangle(16, rowIndex * RowHeight, card.Width - 220, RowHeight),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 11f),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(label);

        control.Location = new Point(
            card.Width - control.Width - 16,
            rowIndex * RowHeight + (RowHeight - control.Height) / 2);
        control.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        card.Controls.Add(control);
    }

    /// <summary>Small circuit-board style glyph used in the header.</summary>
    internal static void DrawAppGlyph(Graphics g, RectangleF rect)
    {
        using var back = new SolidBrush(Color.FromArgb(28, 28, 40));
        using var path = OverlayRenderer.RoundedRect(rect, rect.Width * 0.22f);
        g.FillPath(back, path);
        using (var border = new Pen(Color.FromArgb(70, 123, 97, 255), 1.5f))
            g.DrawPath(border, path);

        float u = rect.Width / 8f;
        PointF P(float x, float y) => new(rect.X + x * u, rect.Y + y * u);
        var green = Color.FromArgb(0, 255, 176);
        var purple = Color.FromArgb(140, 116, 255);

        using (var pen = new Pen(green, u * 0.45f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, new[] { P(2, 6), P(2, 3.4f), P(4.2f, 5.2f), P(4.2f, 2) });
        using (var pen = new Pen(purple, u * 0.45f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, new[] { P(6, 2), P(6, 4.6f), P(4.2f, 2) });

        void Node(PointF p, Color c)
        {
            using var brush = new SolidBrush(c);
            g.FillEllipse(brush, p.X - u * 0.55f, p.Y - u * 0.55f, u * 1.1f, u * 1.1f);
            using var ring = new Pen(Color.White, u * 0.16f);
            g.DrawEllipse(ring, p.X - u * 0.55f, p.Y - u * 0.55f, u * 1.1f, u * 1.1f);
        }

        Node(P(2, 6), green);
        Node(P(4.2f, 2), ColorUtil.Lerp(green, purple, 0.5f));
        Node(P(6, 4.6f), purple);
    }

    /// <summary>Faint grid over the near-black background, as in the reference.</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        using var pen = new Pen(Color.FromArgb(14, 160, 140, 255));
        for (int x = 0; x < Width; x += 28)
            e.Graphics.DrawLine(pen, x, TitleBarHeight, x, Height);
        for (int y = TitleBarHeight; y < Height; y += 28)
            e.Graphics.DrawLine(pen, 0, y, Width, y);
    }

    // ------------------------------------------------------------------
    // Values
    // ------------------------------------------------------------------

    private void LoadFrom(AppSettings s)
    {
        _gradientStart.Selected = ColorUtil.Parse(s.LineGradientStart, Color.FromArgb(0, 255, 176));
        _gradientEnd.Selected = ColorUtil.Parse(s.LineGradientEnd, Color.FromArgb(123, 97, 255));
        _wordColor.Selected = ColorUtil.Parse(s.WordTextColor, Color.FromArgb(0, 255, 176));
        _wordPosition.SelectedIndex = (int)s.WordPosition;
        _wordSize.SelectedIndex = (int)s.WordSize;
        _showBackground.Checked = s.ShowTextBackground;
        _showWordText.Checked = s.ShowWordText;
        _refreshSpeed.SelectedIndex = (int)s.RefreshSpeed;
    }

    private void SaveAndClose()
    {
        Result.LineGradientStart = ColorUtil.ToHex(_gradientStart.Selected);
        Result.LineGradientEnd = ColorUtil.ToHex(_gradientEnd.Selected);
        Result.WordTextColor = ColorUtil.ToHex(_wordColor.Selected);
        Result.WordPosition = (WordPosition)Math.Max(0, _wordPosition.SelectedIndex);
        Result.WordSize = (WordSize)Math.Max(0, _wordSize.SelectedIndex);
        Result.ShowTextBackground = _showBackground.Checked;
        Result.ShowWordText = _showWordText.Checked;
        Result.RefreshSpeed = (RefreshSpeed)Math.Max(0, _refreshSpeed.SelectedIndex);
        DialogResult = DialogResult.OK;
    }

    // ------------------------------------------------------------------
    // Dragging support
    // ------------------------------------------------------------------

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 0x2;

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
}
