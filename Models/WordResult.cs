namespace WordLinkSolver.Models;

/// <summary>
/// A single valid word found on the board, with the tile path that spells it.
/// </summary>
public sealed class WordResult
{
    /// <summary>The word, uppercase A–Z.</summary>
    public string Word { get; }

    /// <summary>
    /// Cell indices (0..15, row-major: index = row * 4 + col) in swipe order.
    /// Path[i] is the tile for Word[i].
    /// </summary>
    public IReadOnlyList<int> Path { get; }

    /// <summary>Total score for the word.</summary>
    public int Score { get; }

    public int Length => Word.Length;

    public WordResult(string word, IReadOnlyList<int> path, int score)
    {
        Word = word;
        Path = path;
        Score = score;
    }

    public override string ToString() => $"{Word} • {Score}";
}
