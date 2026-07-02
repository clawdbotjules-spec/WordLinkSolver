#!/usr/bin/env python3
"""Core solver for 4x4 WordLink-style boards.

Deterministic and offline: a trie built from the curated game dictionary,
walked with a depth-first search that follows the game's path rules
(8-direction adjacency, each tile used at most once). Words are ranked
longest-first — in WordLink the longer word is worth more, and the curated
dictionary already excludes obscure words the game rejects.

Also usable as a command-line solver:

    python3 wordlink_solver.py OTNIFPSNORAGNOAY
"""
from __future__ import annotations

import argparse
import os
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable, Iterator

BOARD_SIZE = 4
CELL_COUNT = BOARD_SIZE * BOARD_SIZE

APP_DIR = Path(__file__).resolve().parent
# PyInstaller unpacks bundled data under _MEIPASS.
RESOURCE_DIR = Path(getattr(sys, "_MEIPASS", APP_DIR))
# When packaged as an .app the source tree is read-only, so user-writable
# state (rejected words, calibration) lives in Application Support instead.
USER_DATA_DIR = (
    Path.home() / "Library" / "Application Support" / "Wordlink Solver"
    if getattr(sys, "frozen", False)
    else APP_DIR
)
USER_DATA_DIR.mkdir(parents=True, exist_ok=True)

GAME_DICT = RESOURCE_DIR / "game_words.txt"
DEFAULT_DICT = str(GAME_DICT if GAME_DICT.exists() else Path("/usr/share/dict/words"))
REJECTED_WORDS_PATH = USER_DATA_DIR / "rejected_words.txt"

_NEIGHBOR_OFFSETS = tuple(
    (dr, dc) for dr in (-1, 0, 1) for dc in (-1, 0, 1) if (dr, dc) != (0, 0)
)


@dataclass(slots=True)
class TrieNode:
    children: dict[str, "TrieNode"] = field(default_factory=dict)
    word: str | None = None


@dataclass(frozen=True, slots=True)
class Solution:
    word: str
    path: tuple[tuple[int, int], ...]

    @property
    def length(self) -> int:
        return len(self.word)

    def path_text(self) -> str:
        return " -> ".join(f"r{r + 1}c{c + 1}" for r, c in self.path)


def normalize_board(raw: str) -> tuple[tuple[str, ...], ...]:
    """Turns any 16-letter string into a 4x4 uppercase board (row-major)."""
    letters = [ch.upper() for ch in raw if ch.isalpha()]
    if len(letters) != CELL_COUNT:
        raise ValueError("board must contain exactly 16 letters")
    return tuple(
        tuple(letters[row * BOARD_SIZE : (row + 1) * BOARD_SIZE])
        for row in range(BOARD_SIZE)
    )


def load_rejected_words(path: Path = REJECTED_WORDS_PATH) -> set[str]:
    """Words the game refused. One per line; '#' comments allowed."""
    if not path.exists():
        return set()
    return {
        line.strip().upper()
        for line in path.read_text(encoding="utf-8", errors="ignore").splitlines()
        if line.strip() and not line.strip().startswith("#")
    }


def reject_word(word: str, path: Path = REJECTED_WORDS_PATH) -> None:
    """Persists a rejected word so it never comes back, across sessions."""
    word = "".join(ch for ch in word.upper() if ch.isalpha())
    if not word or word in load_rejected_words(path):
        return
    with path.open("a", encoding="utf-8") as file:
        file.write(f"{word}\n")


def iter_words(
    path: str,
    min_len: int = 3,
    max_len: int = CELL_COUNT,
    rejected: set[str] | None = None,
) -> Iterator[str]:
    rejected = rejected or set()
    with open(path, "r", encoding="utf-8", errors="ignore") as file:
        for line in file:
            word = line.strip().upper()
            if min_len <= len(word) <= max_len and word.isalpha() and word not in rejected:
                yield word


def build_trie(words: Iterable[str]) -> TrieNode:
    root = TrieNode()
    for word in words:
        node = root
        for letter in word:
            node = node.children.setdefault(letter, TrieNode())
        node.word = word
    return root


def neighbors(row: int, col: int) -> Iterator[tuple[int, int]]:
    for dr, dc in _NEIGHBOR_OFFSETS:
        nr, nc = row + dr, col + dc
        if 0 <= nr < BOARD_SIZE and 0 <= nc < BOARD_SIZE:
            yield nr, nc


def solve_board(board: tuple[tuple[str, ...], ...], trie: TrieNode) -> list[Solution]:
    """Every dictionary word reachable on the board, longest first.

    Each word keeps the first path found for it. Cells that hold anything
    other than A-Z (e.g. '?' from a failed OCR read) simply never match a
    trie edge, so partial boards degrade gracefully.
    """
    found: dict[str, tuple[tuple[int, int], ...]] = {}

    def walk(
        row: int,
        col: int,
        node: TrieNode,
        used: int,
        path: tuple[tuple[int, int], ...],
    ) -> None:
        next_node = node.children.get(board[row][col])
        if next_node is None:
            return  # prune: no dictionary word continues with this prefix

        next_path = path + ((row, col),)
        if next_node.word is not None:
            found.setdefault(next_node.word, next_path)

        next_used = used | (1 << (row * BOARD_SIZE + col))
        for nr, nc in neighbors(row, col):
            if next_used & (1 << (nr * BOARD_SIZE + nc)):
                continue
            walk(nr, nc, next_node, next_used, next_path)

    for row in range(BOARD_SIZE):
        for col in range(BOARD_SIZE):
            walk(row, col, trie, 0, ())

    return sorted(
        (Solution(word, path) for word, path in found.items()),
        key=lambda item: (-item.length, item.word),
    )


def backup_sort_key(candidate: Solution, chosen: Solution) -> tuple[int, int, str]:
    """Orders alternatives so the best backup shares the fewest tiles with the
    chosen word — if the chosen word fails, the backup is still mostly intact."""
    overlap = len(set(chosen.path).intersection(candidate.path))
    return (overlap, -candidate.length, candidate.word)


def disjoint_followups(solutions: list[Solution], chosen: Solution) -> list[Solution]:
    """Words that share no tiles with the chosen word — safe to plan as the
    next swipe before the board refills."""
    used = set(chosen.path)
    return [
        item
        for item in solutions
        if item.word != chosen.word and used.isdisjoint(item.path)
    ]


def print_board(board: tuple[tuple[str, ...], ...]) -> None:
    for row in board:
        print(" ".join(row))


def main() -> int:
    parser = argparse.ArgumentParser(description="Solve a 4x4 WordLink board.")
    parser.add_argument("board", help="16 letters, read left-to-right and top-to-bottom")
    parser.add_argument("--dict", default=DEFAULT_DICT, help="dictionary word list")
    parser.add_argument("--min-len", type=int, default=3)
    parser.add_argument("--limit", type=int, default=20)
    args = parser.parse_args()

    if not os.path.exists(args.dict):
        raise SystemExit(f"dictionary not found: {args.dict}")

    board = normalize_board(args.board)

    load_start = time.perf_counter()
    rejected = load_rejected_words()
    trie = build_trie(iter_words(args.dict, args.min_len, CELL_COUNT, rejected))
    load_ms = (time.perf_counter() - load_start) * 1000

    solve_start = time.perf_counter()
    solutions = solve_board(board, trie)
    solve_ms = (time.perf_counter() - solve_start) * 1000

    print_board(board)
    print(f"\nloaded dictionary in {load_ms:.1f} ms")
    print(f"solved board in {solve_ms:.3f} ms  ({len(solutions)} words)\n")

    for index, solution in enumerate(solutions[: args.limit], start=1):
        print(f"{index:>2}. {solution.word:<16} {solution.path_text()}")

    if solutions:
        best = solutions[0]
        backups = sorted(solutions[1:], key=lambda item: backup_sort_key(item, best))
        print("\nlow-overlap backups after the best word:")
        for solution in backups[:5]:
            print(f" - {solution.word:<16} {solution.path_text()}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
