# WordLink Solver

Fast local helper for 4×4 WordLink-style boards on **macOS**. Mirror the phone
to your Mac, calibrate once, and an always-on-top HUD reads the board, solves
it, and shows the best word with a numbered swipe path — plus alternatives,
follow-ups you can queue, and low-overlap backups.

Solving is deterministic and offline: a trie over a curated ~23k-word game
dictionary, walked with a pruned DFS. No LLMs, no network.

This follows the proven architecture of
[skhinda7/wordlinksolver](https://github.com/skhinda7/wordlinksolver)
(compact HUD beside the game + one-time crop calibration + Apple Vision OCR +
frequency-filtered dictionary), which also provided the dictionary data files.

## Quick start

```bash
# 1. Compile the OCR helper (one time; needs Xcode command-line tools)
swiftc -O fast_ocr.swift -o fast_ocr

# 2. Launch the HUD
python3 hud.py
```

1. Mirror the phone screen to the Mac (iPhone Mirroring / QuickTime).
2. Click **Calibrate**, then drag tightly around the 4×4 letter grid.
3. Click **Read** for one OCR pass, or check **Auto** to re-read continuously
   (the board updates only when a complete, confident read changes).
4. Swipe the shown path in the game. If the game refuses a word, click
   **Reject word** — it's remembered forever and never suggested again.

The crop is saved to `wordlink_config.json` and reused between sessions.
macOS will ask for **Screen Recording** permission on first capture — grant it
to your terminal (System Settings → Privacy & Security) and retry.

No OCR? No problem: the 16 letter cells in the HUD are editable (typing
auto-advances) and **Paste** accepts any 16-letter string, so the solver works
even without calibration — that's also the path on non-Mac systems.

### Manual-entry window

```bash
python3 app.py
```

A larger window with the same solver and no screen capture: type or paste the
board, click alternatives to preview their paths.

### Command-line solver

```bash
python3 wordlink_solver.py OTNIFPSNORAGNOAY
```

Letters are read left-to-right, top-to-bottom:

```text
O T N I
F P S N
O R A G
N O A Y
```

Prints the words longest-first with their `r1c1 -> r2c2` swipe paths, then
low-overlap backups for when the best word fails.

## How it works

| Piece | Approach |
|---|---|
| Capture | One-time calibration: full screenshot (`screencapture -x`), drag-select the grid, crop rect saved to JSON. Reads re-capture the screen and crop the saved rect — nothing tracks windows. |
| Letter OCR | `fast_ocr` (Swift + Apple Vision): uniform 4×4 split of the crop, each cell's letter zone recognized individually (`.fast`, no language correction); the bottom of every tile is skipped so the point-value dots are never read; lookalikes (`0→O`, `1→I`, …) corrected; unreadable cells come back as `?`. |
| Read acceptance | A read is applied only if all 16 cells are clean **and** the board differs from the last applied one — a noisy frame can never wipe a good board. Auto mode re-reads every 650 ms. |
| Solver | Trie + DFS from every cell, 8-direction adjacency, each tile used at most once, prefix pruning. A full board solves in ~1–3 ms. |
| Ranking | Longest word first (that's what scores in WordLink), alphabetical on ties. **Queue next** lists words sharing no tiles with the current pick; **backups** minimize tile overlap so a failed word costs the least. |
| Dictionary | `game_words.txt` (~23k words): the system word list filtered by per-length frequency floors (wordfreq Zipf), minus person names, minus observed game rejections. Far fewer bogus suggestions than a Scrabble list. |
| Rejections | `Reject word` appends to `rejected_words.txt` (in Application Support for the packaged app), which is excluded from every future load. |

## Files

```
hud.py                     Always-on-top HUD (calibrate / read / auto / reject)
app.py                     Manual-entry solver window
wordlink_solver.py         Trie + DFS solver, also a CLI
fast_ocr.swift             Vision OCR helper (build with swiftc)
game_words.txt             Curated game dictionary (~23k words)
people_names.txt           Name blocklist used by the dictionary builder
rejected_words.txt         Words the game refused (grows via Reject button)
build_game_dictionary.py   Regenerates game_words.txt (needs wordfreq)
tests/test_solver.py       Solver tests (python3 -m unittest discover -s tests)
WordlinkHUD.spec           PyInstaller spec for the .app bundle
```

The HUD and solver run on the Python 3 standard library (Tk included) —
`requirements.txt` is only for regenerating the dictionary.

## Packaging a shareable app

```bash
swiftc -O fast_ocr.swift -o fast_ocr
python3 -m pip install -r requirements-build.txt
python3 -m PyInstaller WordlinkHUD.spec
hdiutil create -volname WordlinkHUD -srcfolder dist/WordlinkHUD.app -ov -format UDZO WordlinkHUD.dmg
```

The app is not notarized; on another Mac, right-click → Open the first time.
When packaged, user state (crop config, rejected words) lives in
`~/Library/Application Support/Wordlink Solver/`.

## Regenerating the dictionary

```bash
python3 -m pip install -r requirements.txt
python3 build_game_dictionary.py
```

Uses `/usr/share/dict/words` + wordfreq Zipf thresholds (tunable per length in
the script), the `people_names.txt` blocklist, and `ALWAYS_KEEP`/`ALWAYS_DROP`
overrides for observed game behavior. Drop a `baby-names.csv` (name,percent)
next to the script to rebuild the name blocklist too.

## Performance

Everything slow stays out of the hot path:

- Dictionary load: once at startup (~100 ms, background thread).
- Calibration: once per session, saved to disk.
- OCR read: one screenshot + 16 small Vision passes, well under a second.
- Solve: trie DFS, ~1–3 ms per board.
- HUD redraw: only when letters actually change.
