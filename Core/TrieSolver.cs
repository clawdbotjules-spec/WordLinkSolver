using System.Diagnostics;
using WordLinkSolver.Models;

namespace WordLinkSolver.Core;

/// <summary>One node of the dictionary prefix tree.</summary>
public sealed class TrieNode
{
    public TrieNode?[] Children = new TrieNode?[26];
    public bool IsWord;
}

/// <summary>
/// Dictionary trie + DFS board solver.
///
/// The trie is built once at startup from the word list; solving a 4×4 board is a
/// depth-first search from every cell that follows the WordLink path rules
/// (8-direction adjacency, each tile used at most once) and abandons any branch
/// whose prefix does not exist in the trie. That pruning is what makes a full
/// solve take single-digit milliseconds.
/// </summary>
public sealed class TrieSolver
{
    public const int GridSize = 4;
    public const int CellCount = GridSize * GridSize;
    public const int MaxWordLength = CellCount;

    private readonly TrieNode _root;

    /// <summary>Number of words kept after filtering.</summary>
    public int WordCount { get; }

    /// <summary>How long the dictionary load + trie build took.</summary>
    public long LoadMillis { get; }

    /// <summary>Precomputed neighbor cell indices for each of the 16 cells.</summary>
    private static readonly int[][] Neighbors = BuildNeighbors();

    private TrieSolver(TrieNode root, int wordCount, long loadMillis)
    {
        _root = root;
        WordCount = wordCount;
        LoadMillis = loadMillis;
    }

    /// <summary>
    /// Fast dictionary load: parses the raw bytes in one pass with no per-line
    /// string allocations (the trie only needs letter indices).
    /// </summary>
    public static TrieSolver LoadFromFile(string path)
    {
        var sw = Stopwatch.StartNew();
        var bytes = File.ReadAllBytes(path);
        var root = new TrieNode();
        int count = 0;

        Span<int> letters = stackalloc int[MaxWordLength];
        int i = 0, n = bytes.Length;
        while (i < n)
        {
            int length = 0;
            bool valid = true;
            while (i < n)
            {
                byte b = bytes[i];
                if (b is (byte)'\r' or (byte)'\n')
                    break;
                i++;
                if (!valid)
                    continue;
                int index = b switch
                {
                    >= (byte)'A' and <= (byte)'Z' => b - 'A',
                    >= (byte)'a' and <= (byte)'z' => b - 'a',
                    _ => -1,
                };
                if (index < 0 || length >= MaxWordLength)
                {
                    valid = false;
                    continue;
                }
                letters[length++] = index;
            }
            while (i < n && bytes[i] is (byte)'\r' or (byte)'\n')
                i++;

            if (valid && length >= 3)
            {
                var node = root;
                for (int k = 0; k < length; k++)
                    node = node.Children[letters[k]] ??= new TrieNode();
                if (!node.IsWord)
                {
                    node.IsWord = true;
                    count++;
                }
            }
        }

        sw.Stop();
        return new TrieSolver(root, count, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Builds the trie, keeping only words of length 3..16 made purely of A–Z
    /// (no hyphens or apostrophes).
    /// </summary>
    public static TrieSolver LoadFromWords(IEnumerable<string> words)
    {
        var sw = Stopwatch.StartNew();
        var root = new TrieNode();
        int count = 0;

        foreach (var raw in words)
        {
            var word = raw.Trim();
            if (word.Length is < 3 or > MaxWordLength)
                continue;

            var node = root;
            bool valid = true;
            foreach (var ch in word)
            {
                int i = ch switch
                {
                    >= 'A' and <= 'Z' => ch - 'A',
                    >= 'a' and <= 'z' => ch - 'a',
                    _ => -1,
                };
                if (i < 0)
                {
                    valid = false;
                    break;
                }
                node = node.Children[i] ??= new TrieNode();
            }

            if (valid && !node!.IsWord)
            {
                node.IsWord = true;
                count++;
            }
        }

        sw.Stop();
        return new TrieSolver(root, count, sw.ElapsedMilliseconds);
    }

    private static int[][] BuildNeighbors()
    {
        var result = new int[CellCount][];
        for (int r = 0; r < GridSize; r++)
        {
            for (int c = 0; c < GridSize; c++)
            {
                var list = new List<int>(8);
                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0)
                            continue;
                        int nr = r + dr, nc = c + dc;
                        if (nr >= 0 && nr < GridSize && nc >= 0 && nc < GridSize)
                            list.Add(nr * GridSize + nc);
                    }
                }
                result[r * GridSize + c] = list.ToArray();
            }
        }
        return result;
    }

    /// <summary>
    /// Finds every valid word on the board.
    /// </summary>
    /// <param name="letters">
    /// 16 letters, row-major. Use '\0' for cells whose letter is unknown
    /// (e.g. failed OCR) — those cells are simply never visited.
    /// </param>
    /// <param name="minWordLength">Minimum word length (default 3).</param>
    /// <returns>
    /// Distinct words sorted best-first: score descending, then length
    /// descending (prefer the longer word on score ties), then alphabetical
    /// for determinism. Each word carries the first best path found for it.
    /// </returns>
    public List<WordResult> Solve(IReadOnlyList<char> letters, int minWordLength = 3)
    {
        if (letters.Count != CellCount)
            throw new ArgumentException($"Expected {CellCount} letters, got {letters.Count}.", nameof(letters));

        // Normalize to uppercase indices; -1 marks unusable cells.
        var letterIndex = new int[CellCount];
        var upper = new char[CellCount];
        for (int i = 0; i < CellCount; i++)
        {
            char ch = letters[i];
            if (ch is >= 'a' and <= 'z')
                ch = (char)(ch - 32);
            upper[i] = ch;
            letterIndex[i] = ch is >= 'A' and <= 'Z' ? ch - 'A' : -1;
        }

        var found = new Dictionary<string, WordResult>(StringComparer.Ordinal);
        var path = new int[MaxWordLength];
        var chars = new char[MaxWordLength];

        for (int start = 0; start < CellCount; start++)
            Dfs(start, _root, 0, 0, letterIndex, upper, path, chars, minWordLength, found);

        var results = new List<WordResult>(found.Values);
        results.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
                return byScore;
            int byLength = b.Length.CompareTo(a.Length);
            if (byLength != 0)
                return byLength;
            return string.CompareOrdinal(a.Word, b.Word);
        });
        return results;
    }

    private static void Dfs(
        int cell,
        TrieNode node,
        int visitedMask,
        int depth,
        int[] letterIndex,
        char[] upper,
        int[] path,
        char[] chars,
        int minWordLength,
        Dictionary<string, WordResult> found)
    {
        int li = letterIndex[cell];
        if (li < 0)
            return;

        var next = node.Children[li];
        if (next is null)
            return; // Prune: no dictionary word starts with this prefix.

        visitedMask |= 1 << cell;
        path[depth] = cell;
        chars[depth] = upper[cell];
        depth++;

        if (next.IsWord && depth >= minWordLength)
        {
            var word = new string(chars, 0, depth);
            if (!found.ContainsKey(word))
            {
                var pathCopy = new int[depth];
                Array.Copy(path, pathCopy, depth);
                found[word] = new WordResult(word, pathCopy, Scorer.Score(word));
            }
        }

        foreach (int n in Neighbors[cell])
        {
            if ((visitedMask & (1 << n)) == 0)
                Dfs(n, next, visitedMask, depth, letterIndex, upper, path, chars, minWordLength, found);
        }
    }
}
