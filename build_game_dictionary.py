#!/usr/bin/env python3
"""Regenerates game_words.txt, the curated dictionary the solver plays with.

WordLink only accepts reasonably common words, so the raw system dictionary
(~235k entries on macOS) is filtered three ways:

  1. Frequency: each word must clear a Zipf-frequency floor that loosens with
     length (short obscure words are the game's most common rejections, long
     words are usually accepted).
  2. Person names: first names (from a baby-names popularity CSV, written out
     to people_names.txt) are dropped — the game does not accept them.
  3. Manual overrides: ALWAYS_KEEP / ALWAYS_DROP for observed game behavior.

The checked-in game_words.txt was produced by this process; rerun only if you
want to retune it. Requires macOS's /usr/share/dict/words, `pip install -r
requirements.txt` (wordfreq), and optionally a baby-names.csv next to this
script to refresh people_names.txt.
"""
from __future__ import annotations

import csv
import sys
from collections import defaultdict
from pathlib import Path

APP_DIR = Path(__file__).resolve().parent
SOURCE_DICT = Path("/usr/share/dict/words")
NAMES_SOURCE = APP_DIR / "baby-names.csv"
PEOPLE_NAMES_PATH = APP_DIR / "people_names.txt"
OUTPUT_DICT = APP_DIR / "game_words.txt"

# Minimum Zipf frequency (wordfreq scale, ~1 rare .. ~7 ubiquitous) by length.
MIN_ZIPF_BY_LENGTH = {
    3: 4.15,
    4: 3.70,
    5: 3.25,
    6: 2.90,
    7: 2.65,
    8: 2.45,
    9: 2.30,
    10: 2.15,
    11: 2.00,
    12: 1.90,
    13: 1.80,
    14: 1.70,
    15: 1.60,
    16: 1.50,
}

# Words the game accepts even though the filters would drop them.
ALWAYS_KEEP = {
    "ANODYNE",
    "DEADEYE",
    "EXTRA",
    "RAYON",
    "TANNED",
    "TAXED",
    "TRADE",
    "XENON",
    "YEAR",
}

# Words the game has refused (or that pollute boards) even though the
# filters would keep them.
ALWAYS_DROP = {
    "ADNEX",
    "ANEND",
    "AYOND",
    "AYONT",
    "DENAT",
    "DENDA",
    "DEXTRAD",
    "DEXTRAN",
    "DONAR",
    "DONAX",
    "DONEE",
    "DONEY",
    "DONNE",
    "ENTAD",
    "NENTA",
    "NONDA",
    "NONTRADE",
    "NONYA",
    "NONTAX",
    "NOYADE",
    "TRANEEN",
    "YADE",
    "YEAN",
    "YEAT",
    "YEDE",
    "YOND",
    "YONT",
}

# Names that are also everyday words stay in the dictionary.
NAME_WORD_EXCEPTIONS = {
    "ACE", "AMBER", "APRIL", "ASH", "AUGUST", "BILL", "BLAZE", "BROOK",
    "BROOKE", "CHASE", "CLIFF", "DAWN", "FAITH", "FELICITY", "FERN", "GALE",
    "GRACE", "HARMONY", "HAZEL", "HEATH", "HOPE", "IVY", "JADE", "JAY", "JOY",
    "JUNE", "LAUREL", "LILY", "MAY", "MERRY", "MILES", "NOBLE", "OPAL",
    "PAGE", "PEARL", "PENNY", "PIERCE", "RAVEN", "REED", "RIVER", "ROSE",
    "RUBY", "SAGE", "SKY", "SUMMER", "SUNNY", "VIOLET", "WADE", "WILL",
}


def load_people_names() -> set[str]:
    """Builds the person-name blocklist, refreshing people_names.txt when a
    baby-names.csv (name,percent columns) is available; otherwise reuses the
    checked-in list."""
    if not NAMES_SOURCE.exists():
        if PEOPLE_NAMES_PATH.exists():
            return {
                line.strip().upper()
                for line in PEOPLE_NAMES_PATH.read_text(encoding="utf-8").splitlines()
                if line.strip()
            }
        return set()

    popularity: dict[str, float] = defaultdict(float)
    with NAMES_SOURCE.open("r", encoding="utf-8", newline="") as file:
        for row in csv.DictReader(file):
            name = row["name"].strip().upper()
            if not name.isalpha() or len(name) < 3:
                continue
            popularity[name] = max(popularity[name], float(row["percent"]))

    names = {
        name
        for name, peak_percent in popularity.items()
        if peak_percent >= 0.00020 and name not in NAME_WORD_EXCEPTIONS
    }
    PEOPLE_NAMES_PATH.write_text("\n".join(sorted(names)) + "\n", encoding="utf-8")
    return names


def main() -> int:
    try:
        from wordfreq import zipf_frequency
    except ImportError:
        raise SystemExit("pip install -r requirements.txt (needs wordfreq)")

    if not SOURCE_DICT.exists():
        raise SystemExit(f"source dictionary not found: {SOURCE_DICT}")

    people_names = load_people_names()
    raw_words = [
        line.strip()
        for line in SOURCE_DICT.read_text(encoding="utf-8", errors="ignore").splitlines()
        if line.strip().isalpha()
    ]
    lowercase_words = {word for word in raw_words if word.islower()}

    words: set[str] = set()
    for raw_word in raw_words:
        word = raw_word.upper()
        if not 3 <= len(word) <= 16:
            continue
        if word in ALWAYS_DROP or word in people_names:
            continue
        # Capitalized-only entries are proper nouns unless a lowercase
        # spelling also exists.
        if (
            raw_word != raw_word.lower()
            and raw_word.lower() not in lowercase_words
            and word not in ALWAYS_KEEP
        ):
            continue
        threshold = MIN_ZIPF_BY_LENGTH.get(len(word), 1.5)
        if word in ALWAYS_KEEP or zipf_frequency(word.lower(), "en") >= threshold:
            words.add(word)

    OUTPUT_DICT.write_text("\n".join(sorted(words)) + "\n", encoding="utf-8")
    print(f"Wrote {len(words)} words to {OUTPUT_DICT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
