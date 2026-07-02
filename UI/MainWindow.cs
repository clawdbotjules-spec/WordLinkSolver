using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WordLinkSolver.Core;
using WordLinkSolver.Models;

namespace WordLinkSolver.UI;

/// <summary>
/// The control window. The user resizes/positions it over the WordLink game;
/// its client area (which is transparent — the game shows through) defines the
/// capture region. A separate click-through layered overlay window floats
/// exactly over the client area and draws the swipe path.
///
/// Main loop: capture → grid detect (cached) → per-cell change hash → OCR
/// changed cells → trie solve → render overlay.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MainWindow : Form
{
    private static readonly Color TransparentKey = Color.Magenta;

    private AppSettings _settings;
    private readonly OverlayWindow _overlay = new();
    private readonly OverlayRenderer _renderer = new();
    private readonly SolveSession _session = new();
    private readonly KeyboardHook _hook = new();
    private OcrEngine? _ocr;
    private TrieSolver? _solver;

    private readonly Button _btnSettings = MakeChip("⚙  Settings");
    private readonly Button _btnFreshScan = MakeChip("↻  Fresh Scan");
    private readonly Label _hintBar = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _loopTimer = new();
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _geometryDebounce = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer _hintRevert = new() { Interval = 5000 };

    // Pipeline state (all touched on the UI thread only).
    private Bitmap? _lastCapture;
    private GridDetector.Result? _grid;
    private LetterTile[]? _tiles;
    private ulong[]? _cellHashes;
    private string _lastSolvedLetters = "";
    private bool _busy;
    private bool _freshScanQueued;
    private bool _paused;
    private bool _settingsOpen;
    private bool _hookActive;
    private bool _captureExclusionOk;
    private OverlayStatus _status = OverlayStatus.None;
    private string? _errorMessage;
    private float _spinnerAngle;
    private readonly Dictionary<Keys, long> _lastHotkeyTicks = new();

    private const string HintText = "Space=pause   B=ban   R=reroll   F=fresh scan   Backspace=retake";

    public MainWindow(AppSettings settings)
    {
        _settings = settings;

        Text = "WordLink Solver";
        TopMost = true;
        KeyPreview = true;
        MinimumSize = new Size(340, 400);
        BackColor = TransparentKey;
        TransparencyKey = TransparentKey;
        Icon = LoadAppIcon();

        if (settings.MainWindowBounds is { } saved &&
            Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved.ToRectangle())))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = saved.ToRectangle();
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(560, 680);
        }

        BuildControls();
        BuildTray();

        _loopTimer.Tick += (_, _) => OnLoopTick();
        _animTimer.Tick += (_, _) => { _spinnerAngle = (_spinnerAngle + 16f) % 360f; UpdateOverlay(); };
        _geometryDebounce.Tick += (_, _) => { _geometryDebounce.Stop(); OnGeometrySettled(); };
        _hintRevert.Tick += (_, _) => { _hintRevert.Stop(); _hintBar.Text = HintText; };
        _hook.KeyDown += OnGlobalKeyDown;

        ApplySettings();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "icon.ico");
            if (File.Exists(path))
                return new Icon(path);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"App icon load failed: {ex.Message}");
        }
        return SystemIcons.Application;
    }

    // ------------------------------------------------------------------
    // UI construction
    // ------------------------------------------------------------------

    private static Button MakeChip(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.ChipBack,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 9.5f),
            TabStop = false,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.ControlHover;
        return button;
    }

    private void BuildControls()
    {
        _btnSettings.Width = 100;
        _btnSettings.Location = new Point(8, 8);
        _btnSettings.Click += (_, _) => OpenSettings();

        _btnFreshScan.Width = 116;
        _btnFreshScan.Location = new Point(114, 8);
        _btnFreshScan.Click += (_, _) => FreshScan();

        _hintBar.Dock = DockStyle.Bottom;
        _hintBar.Height = 26;
        _hintBar.BackColor = Color.FromArgb(16, 16, 24);
        _hintBar.ForeColor = Theme.TextMuted;
        _hintBar.Font = new Font("Segoe UI", 8.5f);
        _hintBar.TextAlign = ContentAlignment.MiddleCenter;
        _hintBar.Text = HintText;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Snap to game window", null, (_, _) => SnapToGameWindow());
        menu.Items.Add("Fresh Scan  (F)", null, (_, _) => FreshScan());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _hintBar.ContextMenuStrip = menu;
        _btnSettings.ContextMenuStrip = menu;
        _btnFreshScan.ContextMenuStrip = menu;
        ContextMenuStrip = menu;

        Controls.Add(_btnSettings);
        Controls.Add(_btnFreshScan);
        Controls.Add(_hintBar);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Snap to game window", null, (_, _) => { RestoreFromTray(); SnapToGameWindow(); });
        menu.Items.Add("Fresh Scan", null, (_, _) => FreshScan());
        menu.Items.Add("Settings…", null, (_, _) => { RestoreFromTray(); OpenSettings(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());

        _tray.Icon = Icon;
        _tray.Text = "WordLink Solver";
        _tray.Visible = true;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        SyncOverlay();
        Activate();
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        _overlay.Show(this); // owned → always floats above this window
        SyncOverlay();

        bool mainExcluded = ScreenCapture.TryExcludeFromCapture(Handle);
        _captureExclusionOk = mainExcluded && _overlay.ExcludedFromCapture;
        if (!_captureExclusionOk)
            Trace.WriteLine("Exclude-from-capture unavailable (needs Windows 10 2004+); overlay will blink during scans.");

        _hookActive = _hook.Install();
        _ocr = OcrEngine.Create();

        SetStatus(OverlayStatus.None, error: null);
        UpdateOverlay();
        FlashHint("Loading dictionary…");

        // Load the dictionary off the UI thread, then kick off the first scan.
        Task.Run(() =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "sowpods.txt");
            return TrieSolver.LoadFromFile(path);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _errorMessage = "Failed to load Resources/sowpods.txt — reinstall the app.";
                Trace.WriteLine($"Dictionary load failed: {t.Exception?.GetBaseException()}");
                UpdateOverlay();
                return;
            }

            _solver = t.Result;
            var message = $"Loaded {_solver.WordCount:N0} words in {_solver.LoadMillis}ms";
            Console.WriteLine(message);
            Trace.WriteLine(message);
            FlashHint($"{message}  —  OCR: {_ocr?.BackendName ?? "None"}");

            if (_ocr is null || !_ocr.IsAvailable)
            {
                _errorMessage = "No OCR engine available.\nInstall the Windows English language pack, or place eng.traineddata in a tessdata folder next to the app.";
                UpdateOverlay();
                return;
            }

            FreshScan();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        SyncOverlay();
        _geometryDebounce.Stop();
        _geometryDebounce.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            // Minimize to tray.
            Hide();
            _overlay.Hide();
            return;
        }
        if (!_overlay.Visible && Visible)
            _overlay.Show(this);
        SyncOverlay();
        _geometryDebounce.Stop();
        _geometryDebounce.Start();
    }

    /// <summary>After a move/resize settles, the cached grid geometry is stale.</summary>
    private void OnGeometrySettled()
    {
        _grid = null;
        _cellHashes = null;
        if (_solver is not null && !_paused)
            _ = RunPipelineAsync(forceOcr: false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _settings.MainWindowBounds = SavedBounds.From(
            WindowState == FormWindowState.Normal ? Bounds : RestoreBounds);
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Settings save failed: {ex.Message}");
        }

        _tray.Visible = false;
        _tray.Dispose();
        _hook.Dispose();
        _loopTimer.Stop();
        _animTimer.Stop();
        _ocr?.Dispose();
        _renderer.Dispose();
        _lastCapture?.Dispose();
        base.OnFormClosing(e);
    }

    private void SyncOverlay()
    {
        if (!IsHandleCreated || WindowState == FormWindowState.Minimized)
            return;
        _overlay.SyncBounds(RectangleToScreen(ClientRectangle));
    }

    /// <summary>The screen region that gets captured and OCR'd (client area minus the hint bar).</summary>
    private Rectangle CaptureRegion()
    {
        var rect = RectangleToScreen(ClientRectangle);
        rect.Height = Math.Max(0, rect.Height - _hintBar.Height);
        return rect;
    }

    private void FlashHint(string message)
    {
        _hintBar.Text = message;
        _hintRevert.Stop();
        _hintRevert.Start();
    }

    // ------------------------------------------------------------------
    // Keyboard controls
    // ------------------------------------------------------------------

    private static readonly Keys[] HotkeyList = { Keys.Space, Keys.B, Keys.R, Keys.F, Keys.Back };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (HotkeyList.Contains(key) && (keyData & (Keys.Control | Keys.Alt)) == 0)
        {
            // When the global hook is active it already saw this key; either way
            // swallow it so Space doesn't "click" a focused chip button.
            if (!_hookActive)
                HandleHotkey(key);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnGlobalKeyDown(Keys key)
    {
        if (!HotkeyList.Contains(key) || _settingsOpen)
            return;
        if (!IsHotkeyRelevant())
            return;
        BeginInvoke(() => HandleHotkey(key));
    }

    /// <summary>
    /// Hotkeys act when our app is focused, or when the focused window is the
    /// one under the overlay (i.e. the game being played).
    /// </summary>
    private bool IsHotkeyRelevant()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return false;
        if (fg == Handle || fg == _overlay.Handle)
            return true;

        var region = CaptureRegion();
        if (region.Width <= 0 || region.Height <= 0 || !Visible)
            return false;
        if (!GetWindowRect(fg, out var r))
            return false;
        var fgRect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        var overlap = Rectangle.Intersect(fgRect, region);
        long overlapArea = (long)overlap.Width * overlap.Height;
        return overlapArea >= (long)region.Width * region.Height / 2;
    }

    private void HandleHotkey(Keys key)
    {
        // Debounce key auto-repeat (the low-level hook sees repeats too).
        long now = Environment.TickCount64;
        if (_lastHotkeyTicks.TryGetValue(key, out long last) && now - last < 220)
            return;
        _lastHotkeyTicks[key] = now;

        switch (key)
        {
            case Keys.Space:
                TogglePause();
                break;
            case Keys.B:
                BanCurrentWord();
                break;
            case Keys.R:
                _session.Reroll();
                UpdateOverlay();
                break;
            case Keys.F:
                FreshScan();
                break;
            case Keys.Back:
                Retake();
                break;
        }
    }

    private void TogglePause()
    {
        _paused = !_paused;
        UpdateOverlay();
    }

    private void BanCurrentWord()
    {
        var banned = _session.Current?.Word;
        if (_session.BanCurrent())
            FlashHint($"Banned \"{banned}\" — bans clear on Fresh Scan");
        UpdateOverlay();
    }

    // ------------------------------------------------------------------
    // Main loop
    // ------------------------------------------------------------------

    private void ApplySettings()
    {
        // The timer always runs (it keeps the overlay glued to the window);
        // the pipeline only auto-runs when not in Manual mode.
        _loopTimer.Interval = _settings.RefreshMs > 0 ? _settings.RefreshMs : 100;
        _loopTimer.Start();
        UpdateOverlay();
    }

    private void OnLoopTick()
    {
        SyncOverlay();
        if (_settings.RefreshMs <= 0 || _paused || _busy || _settingsOpen)
            return;
        if (_solver is null || _ocr is null || !_ocr.IsAvailable)
            return;
        if (!Visible || WindowState == FormWindowState.Minimized)
            return;
        _ = RunPipelineAsync(forceOcr: false);
    }

    private void FreshScan()
    {
        if (_solver is null || _ocr is null || !_ocr.IsAvailable)
            return;
        if (_busy)
        {
            // A scan is in flight; run the fresh scan right after it finishes
            // so the F press isn't silently swallowed.
            _freshScanQueued = true;
            return;
        }
        _grid = null;
        _cellHashes = null;
        _lastSolvedLetters = "";
        _session.ClearBans();
        _ = RunPipelineAsync(forceOcr: true, freshScan: true);
    }

    private void Retake()
    {
        if (_solver is null || _ocr is null || !_ocr.IsAvailable)
            return;
        if (_lastCapture is null)
        {
            FreshScan();
            return;
        }
        _ = RunPipelineAsync(forceOcr: true, recapture: false);
    }

    private async Task RunPipelineAsync(bool forceOcr, bool recapture = true, bool freshScan = false)
    {
        if (_busy || _solver is null || _ocr is null)
            return;
        _busy = true;
        try
        {
            var solver = _solver;
            var stopwatch = Stopwatch.StartNew();

            // --- 1. Capture ---
            if (recapture)
            {
                var region = CaptureRegion();
                if (region.Width < 120 || region.Height < 120)
                {
                    SetStatus(OverlayStatus.None, "Window is too small to scan.");
                    return;
                }
                if (freshScan)
                    SetStatus(OverlayStatus.Scanning);

                Bitmap capture;
                if (_captureExclusionOk)
                {
                    capture = ScreenCapture.Capture(region);
                }
                else
                {
                    // Legacy fallback: blink the overlay away for the grab.
                    _overlay.Hide();
                    try
                    {
                        Application.DoEvents();
                        capture = ScreenCapture.Capture(region);
                    }
                    finally
                    {
                        _overlay.Show(this);
                        SyncOverlay();
                    }
                }
                _lastCapture?.Dispose();
                _lastCapture = capture;
            }

            if (_lastCapture is null)
                return;
            var bmp = _lastCapture;

            // --- 2. Grid detection (cached until the window moves / fresh scan) ---
            var pixels = await Task.Run(() => ScreenCapture.GetPixels(bmp));
            if (_grid is null)
            {
                _grid = await Task.Run(() => GridDetector.Detect(pixels, bmp.Width, bmp.Height))
                        ?? GridDetector.UniformGrid(bmp.Width, bmp.Height);
                ApplyGridGeometry(_grid);
            }
            var grid = _grid;

            // --- 3. Change detection ---
            var hashes = await Task.Run(() =>
            {
                var result = new ulong[16];
                for (int i = 0; i < 16; i++)
                    result[i] = GridDetector.HashCell(pixels, bmp.Width, bmp.Height, grid.Cells[i]);
                return result;
            });

            var changed = new List<int>();
            for (int i = 0; i < 16; i++)
            {
                if (forceOcr || _cellHashes is null ||
                    GridDetector.HashDistance(_cellHashes[i], hashes[i]) > GridDetector.SameCellMaxDistance)
                {
                    changed.Add(i);
                }
            }
            _cellHashes = hashes;

            if (changed.Count == 0 && _tiles is not null)
            {
                SetStatus(OverlayStatus.None);
                return; // board unchanged — keep showing the current word
            }

            // --- 4. OCR the changed cells ---
            SetStatus(OverlayStatus.Calculating);
            _tiles ??= CreateTiles(grid);
            var ocrResults = await _ocr.RecognizeCellsAsync(bmp, grid.Cells, changed);
            for (int i = 0; i < changed.Count; i++)
            {
                var tile = _tiles[changed[i]];
                tile.Letter = ocrResults[i].Letter;
                tile.OcrConfidence = ocrResults[i].Confidence;
            }

            // --- 5. Solve (skipped when the letters didn't actually change) ---
            var letters = _tiles.Select(t => t.Letter).ToArray();
            var lettersKey = new string(letters);
            if (freshScan || forceOcr || lettersKey != _lastSolvedLetters)
            {
                var minLength = Math.Max(3, _settings.MinWordLength);
                var results = await Task.Run(() => solver.Solve(letters, minLength));
                _session.SetResults(results);
                _lastSolvedLetters = lettersKey;
                stopwatch.Stop();
                Trace.WriteLine($"Cycle: {changed.Count} cells OCR'd, {results.Count} words, {stopwatch.ElapsedMilliseconds}ms");
            }

            SetStatus(OverlayStatus.None);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Pipeline failed: {ex}");
            SetStatus(OverlayStatus.None, "Scan failed — press F to retry.");
        }
        finally
        {
            _busy = false;
            UpdateOverlay();
            if (_freshScanQueued)
            {
                _freshScanQueued = false;
                FreshScan();
            }
        }
    }

    private static LetterTile[] CreateTiles(GridDetector.Result grid)
    {
        var tiles = new LetterTile[16];
        for (int i = 0; i < 16; i++)
            tiles[i] = new LetterTile(i / 4, i % 4) { CellBounds = grid.Cells[i] };
        return tiles;
    }

    private void ApplyGridGeometry(GridDetector.Result grid)
    {
        if (_tiles is null)
            return;
        for (int i = 0; i < 16; i++)
            _tiles[i].CellBounds = grid.Cells[i];
    }

    // ------------------------------------------------------------------
    // Overlay
    // ------------------------------------------------------------------

    private void SetStatus(OverlayStatus status, string? error = null)
    {
        _status = status;
        _errorMessage = error;
        _animTimer.Enabled = status is OverlayStatus.Scanning or OverlayStatus.Calculating;
        UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        if (!IsHandleCreated || !_overlay.IsHandleCreated || WindowState == FormWindowState.Minimized)
            return;

        string? message = _errorMessage;
        if (message is null)
        {
            if (_solver is null)
                message = "Loading dictionary…";
            else if (_ocr is null || !_ocr.IsAvailable)
                message = "No OCR engine available — see README.";
            else if (_tiles is null && _status == OverlayStatus.None)
                message = "Position the WordLink game under this window\nand press F to scan.";
            else if (_tiles is not null && _status == OverlayStatus.None &&
                     _tiles.All(t => t.Letter == '\0'))
                message = "Couldn't read the board.\nFit the 4×4 grid inside this window and press F.";
            else if (_tiles is not null && _status == OverlayStatus.None &&
                     _session.HasResults && _session.Current is null)
                message = "No words found";
        }

        var frame = new OverlayFrame
        {
            CanvasSize = ClientSize,
            Settings = _settings,
            CellRects = _grid?.Cells,
            Tiles = _tiles,
            Word = _session.Current,
            Paused = _paused,
            Status = _status,
            Message = message,
            SpinnerAngle = _spinnerAngle,
        };

        _overlay.PushFrame(_renderer.Render(frame));
    }

    // ------------------------------------------------------------------
    // Settings dialog
    // ------------------------------------------------------------------

    private void OpenSettings()
    {
        if (_settingsOpen)
            return;
        _settingsOpen = true;
        try
        {
            // The overlay is click-through and always-on-top; hide it so its
            // drawing can't sit on top of the dialog.
            _overlay.Hide();
            using var dialog = new SettingsWindow(_settings.Clone());
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _settings = dialog.Result;
                try
                {
                    _settings.Save();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Settings save failed: {ex.Message}");
                }
                ApplySettings();
            }
        }
        finally
        {
            _settingsOpen = false;
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                _overlay.Show(this);
                SyncOverlay();
                UpdateOverlay();
            }
        }
    }

    // ------------------------------------------------------------------
    // Snap to game window
    // ------------------------------------------------------------------

    /// <summary>
    /// Resizes this window so its client area matches the window currently
    /// visible underneath the board area (the game/emulator window).
    /// </summary>
    private void SnapToGameWindow()
    {
        var region = CaptureRegion();
        var probes = new[]
        {
            new Point(region.X + region.Width / 2, region.Y + region.Height / 2),
            new Point(region.X + region.Width / 2, region.Y + region.Height / 4),
            new Point(region.X + region.Width / 4, region.Y + region.Height / 2),
        };

        foreach (var probe in probes)
        {
            IntPtr hit = WindowFromPoint(new POINT { x = probe.X, y = probe.Y });
            if (hit == IntPtr.Zero)
                continue;
            IntPtr root = GetAncestor(hit, GA_ROOT);
            if (root == IntPtr.Zero || root == Handle || root == _overlay.Handle)
                continue;
            if (!GetWindowRect(root, out var r))
                continue;

            var target = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (target.Width < 200 || target.Height < 200)
                continue;

            // Expand so our *client* area lands on the game window.
            var client = RectangleToScreen(ClientRectangle);
            Bounds = new Rectangle(
                target.X - (client.X - Bounds.X),
                target.Y - (client.Y - Bounds.Y),
                target.Width + (Bounds.Width - client.Width),
                target.Height + (Bounds.Height - client.Height) + _hintBar.Height);
            FlashHint("Snapped to game window — press F to scan");
            return;
        }

        FlashHint("No window found under the board area");
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
}
