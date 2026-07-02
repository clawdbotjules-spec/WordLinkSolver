# WordLink Solver

A Windows desktop app that watches the **WordLink** mobile game (running in an
emulator or mirrored to the desktop), reads the 4×4 letter board with real-time
screen capture + OCR, finds the highest-scoring valid word, and draws the
optimal swipe path as an overlay — in well under 200 ms per cycle.

```
┌─────────────────────────────────────────────┐
│                Main Loop                     │
│  ┌──────────┐  ┌──────────┐  ┌────────────┐ │
│  │  Screen  │→ │   OCR    │→ │   Solver   │ │
│  │  Capture │  │  Engine  │  │  (Trie +   │ │
│  │ (BitBlt) │  │          │  │    DFS)    │ │
│  └──────────┘  └──────────┘  └─────┬──────┘ │
│                                    │        │
│  ┌─────────────────────────────────▼──────┐ │
│  │         Overlay Renderer               │ │
│  │  (Gradient lines + numbered nodes +    │ │
│  │   word label with score)               │ │
│  └────────────────────────────────────────┘ │
└─────────────────────────────────────────────┘
```

## Requirements

- Windows 10 2004+ (build 19041) or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- The Windows **English language pack** (for the built-in Windows OCR — present
  on almost every machine; see *OCR backends* below for the fallback)

## Build & run

```bash
dotnet build -c Release
dotnet run -c Release
```

The repository bundles the SOWPODS dictionary (`Resources/sowpods.txt`,
267,627 usable words) and the app icon, so a plain build produces a working app.

The solution also builds on Linux/macOS agents (compile-only, via
`EnableWindowsTargeting`), and the core logic tests run anywhere:

```bash
dotnet test WordLinkSolver.Tests/WordLinkSolver.Tests.csproj
```

## Using it

1. Start WordLink in your emulator / phone-mirroring window.
2. Launch **WordLink Solver** and drag/resize its window so the 4×4 board fills
   the transparent client area (or right-click the bottom bar → **Snap to game
   window**, then fine-tune).
3. Press **F** (or click **↻ Fresh Scan**). The app finds the tile grid, OCRs
   the letters and immediately shows the best word with a numbered swipe path.
4. Play the word in the game. The app re-scans on a timer, notices the board
   changed, and shows the next word automatically.

The main window stays on top; everything inside it is click-through (the
overlay uses `WS_EX_LAYERED | WS_EX_TRANSPARENT`), so you interact with the
game as if the solver wasn't there. Cells whose OCR looks doubtful get a red
outline — press **Backspace** to re-OCR without re-capturing.

### Keyboard shortcuts

| Key | Action |
|---|---|
| `Space` | Pause / resume — freezes the current result and shows **⏸ PAUSED** |
| `B` | Ban the displayed word and show the next best (ban list clears on Fresh Scan) |
| `R` | Reroll — cycle through the top 10 words without banning |
| `F` | Fresh Scan — re-capture, re-detect the grid, re-OCR, clear bans |
| `Backspace` | Retake — re-OCR the current capture (no new screenshot) |

Shortcuts work while the app **or the game window under it** is focused (a
pass-through low-level keyboard hook that never swallows keys). They are shown
in the bar at the bottom of the window.

### Settings

Open with the **⚙ Settings** button (top-left) or the tray icon. Saved to
`%AppData%\WordLinkSolver\settings.json`.

- **Appearance** — line gradient start/end colors, word text colour, word
  label position (top / middle / bottom) and size (Small / Medium / Large),
  toggles for the label background and the label itself.
- **Performance** — refresh speed: Fast (50 ms) / Normal (100 ms) /
  Slow (250 ms) / Manual (F key only).

## How it works

| Stage | Implementation | Measured |
|---|---|---|
| Capture | `BitBlt` of the window's client region only. The app's own windows are excluded from capture via `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, so the overlay never OCRs itself. | ~2–4 ms |
| Grid detection | Color-threshold for the light tile faces → connected components → filter by size/squareness/fill → cluster into 4 rows × 4 columns. Cached until the window moves or you Fresh Scan; falls back to an even 4×4 split when detection fails. | <10 ms, once |
| Change detection | 64-bit difference-hash per cell; unchanged cells are never re-OCR'd, an unchanged board skips the whole cycle. | <1 ms |
| OCR | `Windows.Media.Ocr` (WinRT). Cells are cropped to the letter zone (the point-value dots are cut off), Otsu-thresholded to black-on-white, and composed into a wide strip so a batch is recognized in one call; misses get individual retries, and lookalikes (`0→O`, `1→I`, `8→B`, …) are corrected with reduced confidence. | ~15–30 ms for 16 cells |
| Solve | SOWPODS in a trie; DFS from every cell with 8-direction adjacency, single-use tiles and trie-prefix pruning. Scoring: Scrabble-style letter values, ties prefer the longer word. All words are kept, sorted, and the ban/reroll state just filters that list. | **0.3 ms** full board |
| Render | GDI+ into an ARGB back-buffer, swapped in with one `UpdateLayeredWindow` call (inherently flicker-free). Gradient polyline with round caps, numbered circles with auto-contrast digits, word label `WORD • SCORE`. | ~1–3 ms |

Dictionary load is a one-pass byte parser (no per-line strings): ~270k words
in a few hundred ms on first start, done on a background thread while the UI
comes up.

### OCR backends

1. **Windows OCR** (`Windows.Media.Ocr`) — default, ships with Windows, fast.
2. **Tesseract 5** — automatic fallback when Windows OCR has no usable
   language. Drop an [`eng.traineddata`](https://github.com/tesseract-ocr/tessdata_fast)
   into a `tessdata` folder next to `WordLinkSolver.exe` to enable it
   (`--psm 10`, A–Z whitelist). Without either backend the overlay explains
   what to install.

### Scoring note

WordLink encodes tile values as dots under each letter; those are not OCR'd.
The solver uses the classic letter-value table
(`A=1 … Q=10 … Z=10`, see `Core/Scorer.cs`), which tracks the game closely and
always prefers the longer word on ties. `Scorer.LengthMultiplier` is the hook
if the game's length bonus is ever mapped out.

## Project structure

```
WordLinkSolver/
├── WordLinkSolver.sln
├── WordLinkSolver.csproj         # net8.0-windows10.0.19041.0, WinForms
├── Program.cs                    # Entry point
├── Core/
│   ├── ScreenCapture.cs          # BitBlt region grab + capture exclusion
│   ├── GridDetector.cs           # Tile detection + cell change hashing (pure)
│   ├── OcrEngine.cs              # WinRT OCR (strip batching) + Tesseract fallback
│   ├── OcrNormalizer.cs          # Lookalike→letter mapping (pure)
│   ├── TrieSolver.cs             # Trie build + DFS word finder (pure)
│   ├── SolveSession.cs           # Ban list + reroll cursor (pure)
│   ├── Scorer.cs                 # Letter values / word scoring (pure)
│   └── KeyboardHook.cs           # Pass-through global hotkeys
├── UI/
│   ├── MainWindow.cs             # Control window, main loop, tray, snapping
│   ├── OverlayWindow.cs          # Layered click-through overlay window
│   ├── OverlayRenderer.cs        # GDI+ drawing (path, nodes, labels, status)
│   ├── SettingsWindow.cs         # Dark settings dialog
│   └── DarkControls.cs           # Theme + toggle switch / pill button / swatch
├── Models/
│   ├── LetterTile.cs             # Letter, grid position, pixel geometry
│   ├── WordResult.cs             # Word, path, score
│   └── AppSettings.cs            # JSON-persisted settings
├── Resources/
│   ├── sowpods.txt               # Word dictionary
│   └── icon.ico                  # App icon
└── WordLinkSolver.Tests/         # xunit tests for the pure core (run on any OS)
```

The files marked *(pure)* have no Windows dependencies and are compiled
directly into the test project, so the solver, grid detector, session logic
and OCR normalization are fully unit-tested on Linux CI as well.

## Edge cases handled

- **Duplicate letters** — paths track visited tiles; a tile is never reused.
- **Game window not found / OCR failure** — the overlay shows a hint
  ("Position the WordLink game under this window and press F to scan");
  unreadable cells are outlined in red and excluded from paths.
- **Board unchanged after a scan** — identical letters skip re-solving;
  identical pixels skip OCR entirely.
- **No words on board** — "No words found" indicator.
- **Multiple monitors / DPI** — the process runs PerMonitorV2-aware and all
  geometry is in physical pixels, so capture and overlay stay aligned wherever
  the window sits.
- **Old Windows without capture-exclusion** — the overlay briefly hides itself
  during captures instead.
