#!/usr/bin/env python3
"""Manual-entry WordLink solver window.

No screen capture or OCR — type or paste the 16 board letters and the best
words appear instantly with their swipe paths. Works on any OS with Python 3
and Tk. For the always-on-top OCR HUD, run ``hud.py`` instead.
"""
from __future__ import annotations

import threading
import time
import tkinter as tk
from tkinter import ttk

from wordlink_solver import (
    BOARD_SIZE,
    CELL_COUNT,
    DEFAULT_DICT,
    Solution,
    backup_sort_key,
    build_trie,
    iter_words,
    normalize_board,
    solve_board,
)

CELL = 76
GAP = 10
PAD = 18
CANVAS_SIZE = PAD * 2 + BOARD_SIZE * CELL + (BOARD_SIZE - 1) * GAP

BG = "#0f0f17"
TEXT = "#edf4f2"
TILE = "#f1ecdc"
TILE_EDGE = "#d3cdbb"
GRADIENT_START = (0x00, 0xFF, 0xB0)
GRADIENT_END = (0x7B, 0x61, 0xFF)


def _lerp_color(a: tuple[int, int, int], b: tuple[int, int, int], t: float) -> str:
    t = min(max(t, 0.0), 1.0)
    return "#%02x%02x%02x" % tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


class WordlinkApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("WordLink Solver")
        self.geometry("780x560")
        self.minsize(740, 520)

        self.trie: object | None = None
        self.entries: list[tk.Entry] = []
        self.letters: list[tk.StringVar] = []
        self.solutions: list[Solution] = []
        self.selected_solution: Solution | None = None
        self.solve_after_id: str | None = None

        self.configure(bg=BG)
        self._build_ui()
        self._load_dictionary()

    def _build_ui(self) -> None:
        style = ttk.Style(self)
        style.theme_use("clam")
        style.configure("TFrame", background=BG)
        style.configure("TLabel", background=BG, foreground=TEXT)
        style.configure("Muted.TLabel", foreground="#9fb3b8")
        style.configure("Word.TLabel", font=("Helvetica", 34, "bold"), foreground="#ffffff")
        style.configure("Status.TLabel", foreground="#7b61ff")
        style.configure("TButton", font=("Helvetica", 13), padding=8)

        root = ttk.Frame(self, padding=16)
        root.pack(fill="both", expand=True)
        root.columnconfigure(1, weight=1)
        root.rowconfigure(1, weight=1)

        header = ttk.Frame(root)
        header.grid(row=0, column=0, columnspan=2, sticky="ew", pady=(0, 14))
        header.columnconfigure(1, weight=1)
        ttk.Label(header, text="WordLink Solver", font=("Helvetica", 24, "bold")).grid(
            row=0, column=0, sticky="w"
        )
        self.status = ttk.Label(header, text="Loading dictionary…", style="Status.TLabel")
        self.status.grid(row=0, column=1, sticky="e")

        left = ttk.Frame(root)
        left.grid(row=1, column=0, sticky="nsw", padx=(0, 20))

        self.canvas = tk.Canvas(
            left, width=CANVAS_SIZE, height=CANVAS_SIZE, bg="#0c3338", highlightthickness=0
        )
        self.canvas.grid(row=0, column=0, pady=(0, 12))

        entry_grid = ttk.Frame(left)
        entry_grid.grid(row=1, column=0)
        for row in range(BOARD_SIZE):
            for col in range(BOARD_SIZE):
                var = tk.StringVar()
                entry = tk.Entry(
                    entry_grid,
                    width=2,
                    justify="center",
                    textvariable=var,
                    font=("Helvetica", 24, "bold"),
                    bg=TILE,
                    fg="#050505",
                    insertbackground="#050505",
                    relief="flat",
                )
                entry.grid(row=row, column=col, padx=4, pady=4, ipady=8)
                entry.bind("<KeyRelease>", lambda _e, index=len(self.entries): self._on_key(index))
                entry.bind("<FocusIn>", lambda e: e.widget.select_range(0, "end"))
                var.trace_add("write", lambda *_: self._schedule_solve())
                self.entries.append(entry)
                self.letters.append(var)

        buttons = ttk.Frame(left)
        buttons.grid(row=2, column=0, sticky="ew", pady=(12, 0))
        ttk.Button(buttons, text="Paste 16 letters", command=self._paste_letters).pack(
            side="left", fill="x", expand=True, padx=(0, 8)
        )
        ttk.Button(buttons, text="Clear", command=self._clear).pack(
            side="left", fill="x", expand=True
        )

        right = ttk.Frame(root)
        right.grid(row=1, column=1, sticky="nsew")
        right.columnconfigure(0, weight=1)
        right.rowconfigure(4, weight=1)

        ttk.Label(right, text="Best word", style="Muted.TLabel").grid(row=0, column=0, sticky="w")
        self.best_word = ttk.Label(right, text="Enter the board", style="Word.TLabel")
        self.best_word.grid(row=1, column=0, sticky="w", pady=(2, 4))
        self.path_text = ttk.Label(right, text="", wraplength=340, font=("Helvetica", 14))
        self.path_text.grid(row=2, column=0, sticky="w", pady=(0, 16))

        ttk.Label(right, text="Alternatives", style="Muted.TLabel").grid(row=3, column=0, sticky="nw")
        self.listbox = tk.Listbox(
            right,
            height=12,
            font=("Menlo", 15),
            bg="#16262d",
            fg=TEXT,
            selectbackground="#7b61ff",
            selectforeground="#ffffff",
            relief="flat",
            activestyle="none",
        )
        self.listbox.grid(row=4, column=0, sticky="nsew", pady=(8, 12))
        self.listbox.bind("<<ListboxSelect>>", self._select_from_list)

        ttk.Label(right, text="Low-overlap backups", style="Muted.TLabel").grid(
            row=5, column=0, sticky="w"
        )
        self.backups = ttk.Label(right, text="", wraplength=340, font=("Helvetica", 13))
        self.backups.grid(row=6, column=0, sticky="w", pady=(8, 0))

        self._draw_board()
        self.entries[0].focus_set()

    def _load_dictionary(self) -> None:
        def worker() -> None:
            started = time.perf_counter()
            trie = build_trie(iter_words(DEFAULT_DICT, 3, CELL_COUNT))
            elapsed = (time.perf_counter() - started) * 1000
            self.after(0, lambda: self._dictionary_loaded(trie, elapsed))

        threading.Thread(target=worker, daemon=True).start()

    def _dictionary_loaded(self, trie, elapsed_ms: float) -> None:
        self.trie = trie
        self.status.configure(text=f"Ready — dictionary loaded in {elapsed_ms:.0f} ms")
        self._schedule_solve()

    def _on_key(self, index: int) -> None:
        value = self.letters[index].get().upper()
        letter = next((ch for ch in value if ch.isalpha()), "")
        if self.letters[index].get() != letter:
            self.letters[index].set(letter)
        if letter and index + 1 < len(self.entries):
            self.entries[index + 1].focus_set()
            self.entries[index + 1].select_range(0, "end")

    def _schedule_solve(self) -> None:
        if self.solve_after_id is not None:
            self.after_cancel(self.solve_after_id)
        self.solve_after_id = self.after(20, self._solve)

    def _solve(self) -> None:
        self.solve_after_id = None
        self._draw_board()
        if self.trie is None:
            return

        raw = "".join(var.get() for var in self.letters)
        if len(raw) != CELL_COUNT:
            self.solutions = []
            self.selected_solution = None
            self.best_word.configure(text="Enter the board")
            self.path_text.configure(text="")
            self.listbox.delete(0, "end")
            self.backups.configure(text="")
            return

        try:
            board = normalize_board(raw)
        except ValueError:
            return

        started = time.perf_counter()
        self.solutions = solve_board(board, self.trie)
        elapsed = (time.perf_counter() - started) * 1000
        self.status.configure(text=f"Solved in {elapsed:.2f} ms — {len(self.solutions)} words")

        self.listbox.delete(0, "end")
        for solution in self.solutions[:30]:
            self.listbox.insert("end", f"{solution.word:<16} {solution.length:>2}")

        if self.solutions:
            self.listbox.selection_set(0)
            self._show_solution(self.solutions[0])
        else:
            self.selected_solution = None
            self.best_word.configure(text="No words")
            self.path_text.configure(text="")
            self.backups.configure(text="")

    def _show_solution(self, solution: Solution) -> None:
        self.selected_solution = solution
        self.best_word.configure(text=solution.word)
        self.path_text.configure(text=solution.path_text())
        self._draw_board(solution)

        backups = sorted(
            (item for item in self.solutions if item.word != solution.word),
            key=lambda item: backup_sort_key(item, solution),
        )[:5]
        self.backups.configure(
            text="\n".join(f"{item.word}  {item.path_text()}" for item in backups)
        )

    def _select_from_list(self, _event) -> None:
        selection = self.listbox.curselection()
        if selection and selection[0] < len(self.solutions):
            self._show_solution(self.solutions[selection[0]])

    def _paste_letters(self) -> None:
        try:
            text = self.clipboard_get()
        except tk.TclError:
            return
        letters = [ch.upper() for ch in text if ch.isalpha()][:CELL_COUNT]
        for index, var in enumerate(self.letters):
            var.set(letters[index] if index < len(letters) else "")

    def _clear(self) -> None:
        for var in self.letters:
            var.set("")
        self.entries[0].focus_set()

    def _draw_board(self, solution: Solution | None = None) -> None:
        self.canvas.delete("all")
        centers: list[tuple[int, int]] = []
        for row in range(BOARD_SIZE):
            for col in range(BOARD_SIZE):
                x = PAD + col * (CELL + GAP)
                y = PAD + row * (CELL + GAP)
                centers.append((x + CELL // 2, y + CELL // 2))
                self.canvas.create_rectangle(
                    x, y, x + CELL, y + CELL, fill=TILE, outline=TILE_EDGE, width=2
                )
                letter = self.letters[row * BOARD_SIZE + col].get().upper()
                if letter:
                    self.canvas.create_text(
                        x + CELL // 2,
                        y + CELL // 2,
                        text=letter,
                        fill="#050505",
                        font=("Helvetica", 34, "bold"),
                    )

        if solution is None:
            return

        points = [centers[row * BOARD_SIZE + col] for row, col in solution.path]
        steps = len(points)
        for i in range(steps - 1):
            color = _lerp_color(GRADIENT_START, GRADIENT_END, i / max(steps - 1, 1))
            self.canvas.create_line(
                points[i][0], points[i][1], points[i + 1][0], points[i + 1][1],
                fill=color, width=8, capstyle="round", joinstyle="round",
            )
        for step, (x, y) in enumerate(points):
            t = step / max(steps - 1, 1)
            rgb = tuple(
                round(GRADIENT_START[i] + (GRADIENT_END[i] - GRADIENT_START[i]) * t)
                for i in range(3)
            )
            luminance = 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]
            self.canvas.create_oval(
                x - 15, y - 15, x + 15, y + 15,
                fill=_lerp_color(GRADIENT_START, GRADIENT_END, t), outline="#ffffff",
            )
            self.canvas.create_text(
                x, y, text=str(step + 1),
                fill="#101018" if luminance > 145 else "#ffffff",
                font=("Helvetica", 13, "bold"),
            )


if __name__ == "__main__":
    WordlinkApp().mainloop()
