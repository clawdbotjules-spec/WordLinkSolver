#!/usr/bin/env python3
"""Always-on-top WordLink HUD.

Flow:
  1. Mirror the phone screen to the Mac (QuickTime / iPhone Mirroring).
  2. Launch ``python3 hud.py``.
  3. Click ``Calibrate`` and drag tightly around the 4x4 letter grid.
  4. Click ``Read`` for a single OCR pass, or enable ``Auto`` to re-read
     continuously. The crop is saved to ``wordlink_config.json`` and reused.

The HUD never draws over the game. It shows its own copy of the board with
the swipe path (gradient line + numbered stops), the best word, alternatives,
disjoint follow-ups you can queue, and low-overlap backups. Letters can
always be typed or pasted manually, so the solver is useful even without
calibration or OCR.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
import time
import tkinter as tk
from pathlib import Path
from tkinter import ttk

from wordlink_solver import (
    BOARD_SIZE,
    CELL_COUNT,
    DEFAULT_DICT,
    REJECTED_WORDS_PATH,
    RESOURCE_DIR,
    USER_DATA_DIR,
    Solution,
    backup_sort_key,
    build_trie,
    disjoint_followups,
    iter_words,
    load_rejected_words,
    normalize_board,
    reject_word,
    solve_board,
)

CELL = 58
GAP = 8
PAD = 12
CANVAS_SIZE = PAD * 2 + BOARD_SIZE * CELL + (BOARD_SIZE - 1) * GAP

CONFIG_PATH = USER_DATA_DIR / "wordlink_config.json"
OCR_BIN = RESOURCE_DIR / "fast_ocr"
FULLSHOT_PATH = USER_DATA_DIR / "calibration_screenshot.png"
DEBUG_CROP_PATH = USER_DATA_DIR / "last_ocr_crop.png"

AUTO_INTERVAL_MS = 650

# Palette
BG = "#0f0f17"
PANEL = "#171720"
PANEL_ALT = "#10272d"
TEXT = "#ecf7f4"
DIM = "#8da6a9"
TILE = "#efe8d4"
TILE_EDGE = "#c8c0aa"
TILE_ON_PATH = "#fff2c0"
GRADIENT_START = (0x00, 0xFF, 0xB0)  # green
GRADIENT_END = (0x7B, 0x61, 0xFF)    # purple


def _lerp_color(a: tuple[int, int, int], b: tuple[int, int, int], t: float) -> str:
    t = min(max(t, 0.0), 1.0)
    return "#%02x%02x%02x" % tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


class HudApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("WordLink HUD")
        self.geometry("640x460+80+80")
        self.minsize(600, 420)
        self.attributes("-topmost", True)
        try:
            self.attributes("-alpha", 0.95)
        except tk.TclError:
            pass

        self.trie: object | None = None
        self.rejected_words = load_rejected_words()
        self.letters = [tk.StringVar() for _ in range(CELL_COUNT)]
        self.entries: list[tk.Entry] = []
        self.solutions: list[Solution] = []
        self.selected_solution: Solution | None = None
        self.solve_after_id: str | None = None
        self.ocr_after_id: str | None = None
        self.auto_ocr = tk.BooleanVar(value=False)
        self.crop_rect: dict[str, float] | None = self._load_crop_rect()
        self.last_ocr_board = ""

        self.configure(bg=BG)
        self._build_ui()
        self._load_dictionary()
        self.protocol("WM_DELETE_WINDOW", self._close)

    # ------------------------------------------------------------------
    # UI construction
    # ------------------------------------------------------------------

    def _build_ui(self) -> None:
        style = ttk.Style(self)
        style.theme_use("clam")
        style.configure("TFrame", background=BG)
        style.configure("Panel.TFrame", background=PANEL)
        style.configure("TLabel", background=BG, foreground=TEXT)
        style.configure("Panel.TLabel", background=PANEL, foreground=TEXT)
        style.configure("Dim.TLabel", background=BG, foreground=DIM)
        style.configure("PanelDim.TLabel", background=PANEL, foreground=DIM)
        style.configure(
            "Best.TLabel", background=PANEL, foreground="#ffffff",
            font=("Helvetica", 30, "bold"),
        )
        style.configure("Tiny.TButton", font=("Helvetica", 11), padding=5)
        style.configure("TCheckbutton", background=BG, foreground=TEXT)

        root = ttk.Frame(self, padding=10)
        root.pack(fill="both", expand=True)
        root.columnconfigure(1, weight=1)
        root.rowconfigure(1, weight=1)

        top = ttk.Frame(root)
        top.grid(row=0, column=0, columnspan=2, sticky="ew", pady=(0, 8))
        top.columnconfigure(1, weight=1)

        ttk.Label(top, text="WordLink HUD", font=("Helvetica", 17, "bold")).grid(
            row=0, column=0, sticky="w"
        )
        self.status = ttk.Label(top, text="Loading…", style="Dim.TLabel")
        self.status.grid(row=0, column=1, sticky="e", padx=8)
        ttk.Button(top, text="Calibrate", style="Tiny.TButton", command=self._calibrate).grid(
            row=0, column=2, padx=(0, 6)
        )
        ttk.Button(top, text="Read", style="Tiny.TButton", command=self._read_ocr_once).grid(
            row=0, column=3, padx=(0, 6)
        )
        ttk.Checkbutton(top, text="Auto", variable=self.auto_ocr, command=self._toggle_auto_ocr).grid(
            row=0, column=4, padx=(0, 6)
        )
        ttk.Button(top, text="Paste", style="Tiny.TButton", command=self._paste_letters).grid(
            row=0, column=5, padx=(0, 6)
        )
        ttk.Button(top, text="Clear", style="Tiny.TButton", command=self._clear).grid(row=0, column=6)

        # Left column: board preview + manual entry grid.
        left = ttk.Frame(root)
        left.grid(row=1, column=0, sticky="nw", padx=(0, 10))

        self.preview_label = ttk.Label(left, text=self._crop_status(), style="Dim.TLabel")
        self.preview_label.grid(row=0, column=0, sticky="w")
        self.canvas = tk.Canvas(
            left, width=CANVAS_SIZE, height=CANVAS_SIZE,
            bg="#062b32", highlightthickness=0,
        )
        self.canvas.grid(row=1, column=0, pady=(6, 10))

        entry_grid = ttk.Frame(left)
        entry_grid.grid(row=2, column=0)
        for row in range(BOARD_SIZE):
            for col in range(BOARD_SIZE):
                index = row * BOARD_SIZE + col
                entry = tk.Entry(
                    entry_grid,
                    width=2,
                    justify="center",
                    textvariable=self.letters[index],
                    font=("Helvetica", 18, "bold"),
                    bg=TILE,
                    fg="#030303",
                    insertbackground="#030303",
                    relief="flat",
                )
                entry.grid(row=row, column=col, padx=3, pady=3, ipady=5)
                entry.bind("<KeyRelease>", lambda _e, i=index: self._on_entry_key(i))
                entry.bind("<FocusIn>", lambda e: e.widget.select_range(0, "end"))
                self.letters[index].trace_add("write", lambda *_: self._schedule_solve())
                self.entries.append(entry)

        # Right column: results panel.
        right = ttk.Frame(root, style="Panel.TFrame", padding=12)
        right.grid(row=1, column=1, sticky="nsew")
        right.columnconfigure(0, weight=1)
        right.rowconfigure(5, weight=1)

        ttk.Label(right, text="Take this", style="PanelDim.TLabel").grid(row=0, column=0, sticky="w")
        self.best_word = ttk.Label(right, text="WAIT", style="Best.TLabel")
        self.best_word.grid(row=1, column=0, sticky="w")
        self.path_text = ttk.Label(
            right, text="", style="Panel.TLabel", wraplength=300, font=("Helvetica", 12)
        )
        self.path_text.grid(row=2, column=0, sticky="ew", pady=(2, 8))

        reject_row = ttk.Frame(right, style="Panel.TFrame")
        reject_row.grid(row=3, column=0, sticky="w", pady=(0, 8))
        ttk.Button(
            reject_row, text="Reject word", style="Tiny.TButton", command=self._reject_selected
        ).pack(side="left")
        ttk.Label(
            reject_row, text="if the game refuses it", style="PanelDim.TLabel"
        ).pack(side="left", padx=8)

        ttk.Label(right, text="Best options", style="PanelDim.TLabel").grid(row=4, column=0, sticky="w")
        self.options = tk.Listbox(
            right,
            height=7,
            font=("Menlo", 14, "bold"),
            bg=PANEL_ALT,
            fg="#f4fbf9",
            selectbackground="#7b61ff",
            selectforeground="#ffffff",
            relief="flat",
            activestyle="none",
        )
        self.options.grid(row=5, column=0, sticky="nsew", pady=(5, 10))
        self.options.bind("<<ListboxSelect>>", self._on_option_selected)

        ttk.Label(right, text="Queue next (no shared tiles)", style="PanelDim.TLabel").grid(
            row=6, column=0, sticky="w"
        )
        self.next_words = ttk.Label(
            right, text="", style="Panel.TLabel", wraplength=300, font=("Helvetica", 12)
        )
        self.next_words.grid(row=7, column=0, sticky="ew", pady=(5, 10))

        ttk.Label(right, text="If letters refill slowly", style="PanelDim.TLabel").grid(
            row=8, column=0, sticky="w"
        )
        self.backups = ttk.Label(
            right, text="", style="Panel.TLabel", wraplength=300, font=("Helvetica", 12)
        )
        self.backups.grid(row=9, column=0, sticky="ew", pady=(5, 0))

        self._draw_board()
        self.entries[0].focus_set()

    # ------------------------------------------------------------------
    # Dictionary
    # ------------------------------------------------------------------

    def _load_dictionary(self) -> None:
        def worker() -> None:
            started = time.perf_counter()
            trie = build_trie(
                iter_words(DEFAULT_DICT, 3, CELL_COUNT, self.rejected_words)
            )
            elapsed = (time.perf_counter() - started) * 1000
            self.after(0, lambda: self._on_dictionary_loaded(trie, elapsed))

        threading.Thread(target=worker, daemon=True).start()

    def _on_dictionary_loaded(self, trie, elapsed_ms: float) -> None:
        self.trie = trie
        self.status.configure(text=f"ready · dictionary {elapsed_ms:.0f} ms")
        self._schedule_solve()

    # ------------------------------------------------------------------
    # Manual entry
    # ------------------------------------------------------------------

    def _on_entry_key(self, index: int) -> None:
        raw = self.letters[index].get().upper()
        letter = next((ch for ch in raw if ch.isalpha()), "")
        if raw != letter:
            self.letters[index].set(letter)
        if letter and index + 1 < len(self.entries):
            self.entries[index + 1].focus_set()
            self.entries[index + 1].select_range(0, "end")

    def _paste_letters(self) -> None:
        try:
            text = self.clipboard_get()
        except tk.TclError:
            return
        letters = [ch.upper() for ch in text if ch.isalpha()][:CELL_COUNT]
        for index, variable in enumerate(self.letters):
            variable.set(letters[index] if index < len(letters) else "")

    def _clear(self) -> None:
        for variable in self.letters:
            variable.set("")
        self.last_ocr_board = ""
        self.entries[0].focus_set()

    # ------------------------------------------------------------------
    # Calibration
    # ------------------------------------------------------------------

    def _load_crop_rect(self) -> dict[str, float] | None:
        if not CONFIG_PATH.exists():
            return None
        try:
            data = json.loads(CONFIG_PATH.read_text())
        except (OSError, json.JSONDecodeError):
            return None
        rect = data.get("crop_rect")
        if not isinstance(rect, dict):
            return None
        keys = ("x", "y", "width", "height")
        if not all(isinstance(rect.get(key), (int, float)) for key in keys):
            return None
        loaded = {key: float(rect[key]) for key in keys}
        loaded["scale"] = float(rect.get("scale", 1.0))
        return loaded

    def _save_crop_rect(self) -> None:
        if self.crop_rect is not None:
            CONFIG_PATH.write_text(json.dumps({"crop_rect": self.crop_rect}, indent=2) + "\n")

    def _crop_status(self) -> str:
        if self.crop_rect is None:
            return "OCR: calibrate the grid crop first"
        rect = self.crop_rect
        return f"OCR crop: {rect['width']:.0f}×{rect['height']:.0f} px"

    def _calibrate(self) -> None:
        """Full-screen screenshot, then drag-select the 4x4 grid on it."""
        self.status.configure(text="capturing screen…")
        self.withdraw()
        self.update_idletasks()
        time.sleep(0.25)  # let the HUD leave the screen before the shot
        try:
            completed = subprocess.run(
                ["/usr/sbin/screencapture", "-x", str(FULLSHOT_PATH)],
                capture_output=True,
                text=True,
                timeout=3.0,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            self.deiconify()
            self.attributes("-topmost", True)
            self.status.configure(text=f"screenshot failed: {error}")
            return

        self.deiconify()
        self.attributes("-topmost", True)

        if completed.returncode != 0:
            message = completed.stderr.strip() or (
                "grant Screen Recording permission (System Settings → Privacy) and retry"
            )
            self.status.configure(text=message[:90])
            return

        selector = tk.Toplevel(self)
        selector.title("Drag around the 4x4 letter grid")
        selector.attributes("-topmost", True)
        selector.configure(bg=BG)

        image = tk.PhotoImage(file=str(FULLSHOT_PATH))
        usable_w = max(1, self.winfo_screenwidth() - 80)
        usable_h = max(1, self.winfo_screenheight() - 140)
        display_scale = max(
            1, int(max(image.width() / usable_w, image.height() / usable_h) + 0.999)
        )
        display_image = image.subsample(display_scale, display_scale)
        # Keep references alive for the lifetime of the window.
        selector._full_image = image  # type: ignore[attr-defined]
        selector._display_image = display_image  # type: ignore[attr-defined]

        ttk.Label(
            selector,
            text="Drag tightly around the 4x4 letter grid, then release. Esc cancels.",
        ).pack(fill="x", padx=10, pady=8)

        canvas = tk.Canvas(
            selector,
            width=display_image.width(),
            height=display_image.height(),
            highlightthickness=0,
            cursor="crosshair",
        )
        canvas.pack(padx=10, pady=(0, 10))
        canvas.create_image(0, 0, anchor="nw", image=display_image)

        drag_start: dict[str, int] = {}
        marquee: list[int | None] = [None]

        def begin(event) -> None:
            drag_start["x"], drag_start["y"] = event.x, event.y
            marquee[0] = canvas.create_rectangle(
                event.x, event.y, event.x, event.y, outline="#00e5ff", width=3
            )

        def drag(event) -> None:
            if marquee[0] is not None:
                canvas.coords(marquee[0], drag_start["x"], drag_start["y"], event.x, event.y)

        def finish(event) -> None:
            x1 = min(drag_start.get("x", event.x), event.x)
            y1 = min(drag_start.get("y", event.y), event.y)
            x2 = max(drag_start.get("x", event.x), event.x)
            y2 = max(drag_start.get("y", event.y), event.y)
            if x2 - x1 >= 80 and y2 - y1 >= 80:
                # Store in full-resolution screenshot pixels; the OCR helper
                # captures at the same native resolution, so scale is 1.
                self.crop_rect = {
                    "x": float(x1 * display_scale),
                    "y": float(y1 * display_scale),
                    "width": float((x2 - x1) * display_scale),
                    "height": float((y2 - y1) * display_scale),
                    "scale": 1.0,
                }
                self._save_crop_rect()
                self.preview_label.configure(text=self._crop_status())
                self._read_ocr_once()
            selector.destroy()

        canvas.bind("<ButtonPress-1>", begin)
        canvas.bind("<B1-Motion>", drag)
        canvas.bind("<ButtonRelease-1>", finish)
        selector.bind("<Escape>", lambda _e: selector.destroy())

    # ------------------------------------------------------------------
    # OCR
    # ------------------------------------------------------------------

    def _toggle_auto_ocr(self) -> None:
        if self.auto_ocr.get():
            self._read_ocr_once()
        elif self.ocr_after_id is not None:
            self.after_cancel(self.ocr_after_id)
            self.ocr_after_id = None

    def _schedule_next_auto_read(self) -> None:
        if self.auto_ocr.get():
            self.ocr_after_id = self.after(AUTO_INTERVAL_MS, self._read_ocr_once)

    def _read_ocr_once(self) -> None:
        if self.crop_rect is None:
            self.status.configure(text="calibrate first")
            return
        if not OCR_BIN.exists():
            self.status.configure(
                text="OCR helper missing — run: swiftc -O fast_ocr.swift -o fast_ocr"
            )
            return

        rect = self.crop_rect

        def worker() -> None:
            started = time.perf_counter()
            try:
                completed = subprocess.run(
                    [
                        str(OCR_BIN),
                        str(rect["x"]),
                        str(rect["y"]),
                        str(rect["width"]),
                        str(rect["height"]),
                        str(rect.get("scale", 1.0)),
                    ],
                    capture_output=True,
                    text=True,
                    timeout=1.5,
                    check=False,
                    env={**os.environ, "WORDLINK_DEBUG_CROP": str(DEBUG_CROP_PATH)},
                )
            except (OSError, subprocess.TimeoutExpired) as error:
                message = str(error)
                self.after(0, lambda: self._ocr_failed(message))
                return

            elapsed = (time.perf_counter() - started) * 1000
            if completed.returncode != 0:
                error = completed.stderr.strip() or "OCR failed"
                self.after(0, lambda message=error: self._ocr_failed(message))
                return
            lines = completed.stdout.splitlines()
            board = lines[0].strip().upper() if lines else ""
            confidence = lines[1].strip() if len(lines) > 1 else ""
            self.after(0, lambda: self._ocr_finished(board, confidence, elapsed))

        threading.Thread(target=worker, daemon=True).start()

    def _ocr_failed(self, message: str) -> None:
        self.status.configure(text=message[:90])
        self._schedule_next_auto_read()

    def _ocr_finished(self, board: str, confidence: str, elapsed_ms: float) -> None:
        clean = "".join(ch for ch in board if ch.isalpha() or ch == "?")[:CELL_COUNT]
        # Only apply complete, confident reads, and only when the board
        # actually changed — a noisy frame never wipes out a good board.
        if len(clean) == CELL_COUNT and "?" not in clean and clean != self.last_ocr_board:
            self.last_ocr_board = clean
            for index, variable in enumerate(self.letters):
                variable.set(clean[index])
        self.status.configure(text=f"OCR {elapsed_ms:.0f} ms  {clean} {confidence}".strip()[:90])
        self._schedule_next_auto_read()

    # ------------------------------------------------------------------
    # Solving + results panel
    # ------------------------------------------------------------------

    def _schedule_solve(self) -> None:
        if self.solve_after_id is not None:
            self.after_cancel(self.solve_after_id)
        self.solve_after_id = self.after(15, self._solve)

    def _solve(self) -> None:
        self.solve_after_id = None
        self._draw_board()

        raw = "".join(item.get() for item in self.letters)
        if self.trie is None or len(raw) != CELL_COUNT:
            self.best_word.configure(text="WAIT")
            self.path_text.configure(text="Fill all 16 cells (or Calibrate + Read)")
            self.options.delete(0, "end")
            self.next_words.configure(text="")
            self.backups.configure(text="")
            return

        try:
            board = normalize_board(raw)
        except ValueError:
            return

        started = time.perf_counter()
        self.solutions = [
            item
            for item in solve_board(board, self.trie)
            if item.word not in self.rejected_words
        ]
        elapsed = (time.perf_counter() - started) * 1000
        self.status.configure(text=f"{len(self.solutions)} words · {elapsed:.2f} ms")

        self.options.delete(0, "end")
        for solution in self.solutions[:12]:
            self.options.insert("end", f"{solution.word:<14} {solution.length:>2}")

        if not self.solutions:
            self.best_word.configure(text="NONE")
            self.path_text.configure(text="")
            self.next_words.configure(text="")
            self.backups.configure(text="")
            return

        self.options.selection_set(0)
        self._show_solution(self.solutions[0])

    def _show_solution(self, solution: Solution) -> None:
        self.selected_solution = solution
        self.best_word.configure(text=solution.word)
        self.path_text.configure(text=solution.path_text())
        self._draw_board(solution)

        followups = disjoint_followups(self.solutions, solution)
        self.next_words.configure(
            text=self._format_word_lines(followups[:5])
            or "No clean follow-up on the current board"
        )
        backups = sorted(
            (item for item in self.solutions if item.word != solution.word),
            key=lambda item: backup_sort_key(item, solution),
        )
        self.backups.configure(text=self._format_word_lines(backups[:5]))

    @staticmethod
    def _format_word_lines(solutions: list[Solution]) -> str:
        return "\n".join(f"{item.word:<12} {item.path_text()}" for item in solutions)

    def _on_option_selected(self, _event) -> None:
        selection = self.options.curselection()
        if selection and selection[0] < len(self.solutions):
            self._show_solution(self.solutions[selection[0]])

    def _reject_selected(self) -> None:
        """The game refused the word: persist it and move to the next best."""
        if self.selected_solution is None:
            return
        word = self.selected_solution.word
        reject_word(word)
        self.rejected_words.add(word)
        self.solutions = [item for item in self.solutions if item.word != word]

        self.options.delete(0, "end")
        for solution in self.solutions[:12]:
            self.options.insert("end", f"{solution.word:<14} {solution.length:>2}")

        if self.solutions:
            self.options.selection_set(0)
            self._show_solution(self.solutions[0])
        else:
            self.selected_solution = None
            self.best_word.configure(text="NONE")
            self.path_text.configure(text="")
            self.next_words.configure(text="")
            self.backups.configure(text="")
        self.status.configure(text=f"rejected {word} · saved to {REJECTED_WORDS_PATH.name}")

    # ------------------------------------------------------------------
    # Board canvas
    # ------------------------------------------------------------------

    def _draw_board(self, solution: Solution | None = None) -> None:
        self.canvas.delete("all")
        centers: list[tuple[int, int]] = []
        path_cells = set(solution.path) if solution else set()

        for row in range(BOARD_SIZE):
            for col in range(BOARD_SIZE):
                x = PAD + col * (CELL + GAP)
                y = PAD + row * (CELL + GAP)
                centers.append((x + CELL // 2, y + CELL // 2))
                fill = TILE_ON_PATH if (row, col) in path_cells else TILE
                self.canvas.create_rectangle(
                    x, y, x + CELL, y + CELL, fill=fill, outline=TILE_EDGE, width=2
                )
                letter = self.letters[row * BOARD_SIZE + col].get().upper()
                if letter:
                    self.canvas.create_text(
                        x + CELL // 2,
                        y + CELL // 2,
                        text=letter,
                        fill="#040404",
                        font=("Helvetica", 26, "bold"),
                    )

        if solution is None:
            return

        points = [centers[row * BOARD_SIZE + col] for row, col in solution.path]
        steps = len(points)

        # Gradient swipe path: each segment interpolates green -> purple.
        for i in range(steps - 1):
            t0 = i / max(steps - 1, 1)
            color = _lerp_color(GRADIENT_START, GRADIENT_END, t0)
            self.canvas.create_line(
                points[i][0], points[i][1], points[i + 1][0], points[i + 1][1],
                fill=color, width=7, capstyle="round", joinstyle="round",
            )

        # Numbered stops with auto-contrast digits.
        for step, (x, y) in enumerate(points):
            t = step / max(steps - 1, 1)
            rgb = tuple(
                round(GRADIENT_START[i] + (GRADIENT_END[i] - GRADIENT_START[i]) * t)
                for i in range(3)
            )
            luminance = 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]
            fg = "#101018" if luminance > 145 else "#ffffff"
            self.canvas.create_oval(
                x - 13, y - 13, x + 13, y + 13,
                fill=_lerp_color(GRADIENT_START, GRADIENT_END, t), outline="#ffffff",
            )
            self.canvas.create_text(
                x, y, text=str(step + 1), fill=fg, font=("Helvetica", 11, "bold")
            )

    # ------------------------------------------------------------------

    def _close(self) -> None:
        if self.ocr_after_id is not None:
            self.after_cancel(self.ocr_after_id)
        if self.solve_after_id is not None:
            self.after_cancel(self.solve_after_id)
        self.destroy()


if __name__ == "__main__":
    HudApp().mainloop()
