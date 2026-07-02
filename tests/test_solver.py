"""Solver tests — run with:  python3 -m unittest discover -s tests -v"""
from __future__ import annotations

import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from wordlink_solver import (  # noqa: E402
    BOARD_SIZE,
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

GAME_DICT_PATH = Path(__file__).resolve().parent.parent / "game_words.txt"


def board_from(flat: str):
    return normalize_board(flat)


def words_of(solutions: list[Solution]) -> list[str]:
    return [item.word for item in solutions]


class NormalizeBoardTests(unittest.TestCase):
    def test_accepts_exactly_sixteen_letters(self):
        board = board_from("abcdOFGHijklMNPQ")
        self.assertEqual(board[0], ("A", "B", "C", "D"))
        self.assertEqual(board[3], ("M", "N", "P", "Q"))

    def test_strips_non_letters(self):
        board = board_from("a b-c d\nefgh ijkl mnpq!")
        self.assertEqual(board[0], ("A", "B", "C", "D"))

    def test_rejects_wrong_length(self):
        with self.assertRaises(ValueError):
            board_from("ABC")
        with self.assertRaises(ValueError):
            board_from("A" * 17)


class SolveBoardTests(unittest.TestCase):
    def test_finds_horizontal_and_extended_words(self):
        trie = build_trie(["CAT", "CATS", "DOG"])
        solutions = solve_board(board_from("CATSXXXXXXXXXXXX"), trie)
        self.assertIn("CAT", words_of(solutions))
        self.assertIn("CATS", words_of(solutions))
        self.assertNotIn("DOG", words_of(solutions))

    def test_finds_diagonal_paths(self):
        trie = build_trie(["ABC"])
        solutions = solve_board(board_from("AXXXXBXXXXCXXXXX"), trie)
        self.assertEqual(words_of(solutions), ["ABC"])
        self.assertEqual(solutions[0].path, ((0, 0), (1, 1), (2, 2)))

    def test_rejects_non_adjacent_letters(self):
        trie = build_trie(["ABE"])
        # A at (0,0), B at (0,2): not adjacent.
        solutions = solve_board(board_from("AXBEXXXXXXXXXXXX"), trie)
        self.assertEqual(solutions, [])

    def test_never_reuses_a_tile(self):
        trie = build_trie(["AAA"])
        # Only two As on the board.
        solutions = solve_board(board_from("AAXXXXXXXXXXXXXX"), trie)
        self.assertEqual(solutions, [])

    def test_deduplicates_words_found_via_multiple_paths(self):
        trie = build_trie(["TAT"])
        solutions = solve_board(board_from("TATTXXXXXXXXXXXX"), trie)
        self.assertEqual(len(solutions), 1)

    def test_orders_longest_first_then_alphabetical(self):
        trie = build_trie(["TEE", "TEN", "TEEN"])
        # T E N X
        # X E X X   — TEE, TEN and TEEN are all reachable.
        solutions = solve_board(board_from("TENXXEXXXXXXXXXX"), trie)
        # TEEN (4) first; TEE and TEN tie on length and sort alphabetically.
        self.assertEqual(words_of(solutions), ["TEEN", "TEE", "TEN"])

    def test_unreadable_cells_degrade_gracefully(self):
        trie = build_trie(["CAT"])
        # '?' occupies the A cell: no words possible, no crash.
        board = (("C", "?", "T", "S"), ("X",) * 4, ("X",) * 4, ("X",) * 4)
        self.assertEqual(solve_board(board, trie), [])

    def test_paths_are_valid_adjacent_unique_and_spell_the_word(self):
        trie = build_trie(["MISER", "SCREAM", "CREAM", "RECANT", "TRANCE", "CANE", "RACE"])
        board = board_from("MISCTNARXXEXXXXX")
        solutions = solve_board(board, trie)
        self.assertTrue(solutions)
        for solution in solutions:
            spelled = "".join(board[r][c] for r, c in solution.path)
            self.assertEqual(spelled, solution.word)
            self.assertEqual(len(set(solution.path)), len(solution.path))
            for (r1, c1), (r2, c2) in zip(solution.path, solution.path[1:]):
                self.assertLessEqual(abs(r1 - r2), 1)
                self.assertLessEqual(abs(c1 - c2), 1)
                self.assertNotEqual((r1, c1), (r2, c2))


class WordFilterTests(unittest.TestCase):
    def test_iter_words_filters_length_alpha_and_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "words.txt"
            path.write_text("ab\ncat\nCAT'S\ntoolongtoolongtoolong\ndog\nDOG\nrejected\n")
            words = list(iter_words(str(path), 3, 16, rejected={"REJECTED"}))
        self.assertEqual(words, ["CAT", "DOG", "DOG"])

    def test_rejected_words_persist_and_reload(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "rejected_words.txt"
            reject_word("qat!", path)
            reject_word("QAT", path)  # duplicate ignored
            reject_word("zonal", path)
            self.assertEqual(load_rejected_words(path), {"QAT", "ZONAL"})
            # Comments and blanks are ignored.
            path.write_text("# note\n\nFOO\n")
            self.assertEqual(load_rejected_words(path), {"FOO"})


class RankingHelperTests(unittest.TestCase):
    def setUp(self):
        self.chosen = Solution("ABC", ((0, 0), (0, 1), (0, 2)))

    def test_backup_prefers_low_overlap_then_length(self):
        heavy_overlap = Solution("ABCD", ((0, 0), (0, 1), (0, 2), (0, 3)))
        no_overlap_short = Solution("XYZ", ((3, 0), (3, 1), (3, 2)))
        no_overlap_long = Solution("WXYZ", ((2, 0), (3, 0), (3, 1), (3, 2)))
        ranked = sorted(
            [heavy_overlap, no_overlap_short, no_overlap_long],
            key=lambda item: backup_sort_key(item, self.chosen),
        )
        self.assertEqual(words_of(ranked), ["WXYZ", "XYZ", "ABCD"])

    def test_disjoint_followups_share_no_tiles(self):
        disjoint = Solution("XYZ", ((3, 0), (3, 1), (3, 2)))
        touching = Solution("QRS", ((0, 2), (1, 2), (2, 2)))
        result = disjoint_followups([self.chosen, disjoint, touching], self.chosen)
        self.assertEqual(words_of(result), ["XYZ"])


class GameDictionaryTests(unittest.TestCase):
    def test_game_dictionary_is_clean(self):
        words = [w.strip() for w in GAME_DICT_PATH.read_text().splitlines() if w.strip()]
        self.assertGreater(len(words), 20_000)
        for word in words:
            self.assertTrue(word.isalpha() and word.isupper())
            self.assertTrue(3 <= len(word) <= BOARD_SIZE * BOARD_SIZE)

    def test_readme_example_board_solves_fast_with_real_dictionary(self):
        trie = build_trie(iter_words(str(GAME_DICT_PATH)))
        board = board_from("OTNIFPSNORAGNOAY")

        started = time.perf_counter()
        solutions = solve_board(board, trie)
        elapsed_ms = (time.perf_counter() - started) * 1000

        self.assertGreater(len(solutions), 30)
        # Longest-first ordering.
        lengths = [item.length for item in solutions]
        self.assertEqual(lengths, sorted(lengths, reverse=True))
        # Every path must be valid on the board.
        for solution in solutions[:50]:
            spelled = "".join(board[r][c] for r, c in solution.path)
            self.assertEqual(spelled, solution.word)
        self.assertLess(elapsed_ms, 500, f"solve took {elapsed_ms:.1f} ms")


if __name__ == "__main__":
    unittest.main()
