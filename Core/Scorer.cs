namespace WordLinkSolver.Core;

/// <summary>
/// Word scoring. Uses a Scrabble-style letter value table; the WordLink game's
/// exact values are encoded by the dots under each tile, which are not OCR'd —
/// this table is a close, deterministic stand-in. Ties between equal scores are
/// broken by preferring the longer word (see <see cref="TrieSolver"/> ordering).
/// </summary>
public static class Scorer
{
    // A  B  C  D  E  F  G  H  I  J  K  L  M  N  O  P  Q   R  S  T  U  V  W  X  Y  Z
    private static readonly int[] Values =
    {
        1, 3, 3, 2, 1, 4, 2, 4, 1, 8, 5, 1, 3, 1, 1, 3, 10, 1, 1, 1, 1, 4, 4, 8, 4, 10,
    };

    /// <summary>Point value for a single letter; 0 for anything outside A–Z.</summary>
    public static int LetterValue(char c)
    {
        if (c is >= 'a' and <= 'z')
            c = (char)(c - 32);
        if (c is < 'A' or > 'Z')
            return 0;
        return Values[c - 'A'];
    }

    /// <summary>
    /// Score for a whole word: sum of letter values times the length multiplier.
    /// </summary>
    public static int Score(string word)
    {
        int sum = 0;
        foreach (var c in word)
            sum += LetterValue(c);
        return (int)Math.Round(sum * LengthMultiplier(word.Length));
    }

    /// <summary>
    /// Length bonus hook. WordLink may multiply long words; the default is a
    /// flat 1.0 (no bonus). Adjust here if the game's scoring is discovered to
    /// use multipliers — the solver's ordering picks longer words on ties
    /// either way.
    /// </summary>
    public static double LengthMultiplier(int length) => 1.0;
}
