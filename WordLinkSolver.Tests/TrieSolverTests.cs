using WordLinkSolver.Core;
using Xunit;

namespace WordLinkSolver.Tests;

public class TrieSolverTests
{
    private static char[] Grid(string flat)
    {
        Assert.Equal(16, flat.Length);
        return flat.ToCharArray();
    }

    [Fact]
    public void FindsWordsAlongHorizontalAndAdjacentPaths()
    {
        var solver = TrieSolver.LoadFromWords(new[] { "CAT", "CATS", "DOG" });
        // C A T S
        // X X X X
        // X X X X
        // X X X X
        var results = solver.Solve(Grid("CATSXXXXXXXXXXXX"));

        var words = results.Select(r => r.Word).ToList();
        Assert.Contains("CAT", words);
        Assert.Contains("CATS", words);
        Assert.DoesNotContain("DOG", words); // letters not on board
    }

    [Fact]
    public void FindsDiagonalPaths()
    {
        var solver = TrieSolver.LoadFromWords(new[] { "ABC" });
        // A . . .
        // . B . .
        // . . C .
        // . . . .
        var results = solver.Solve(Grid("AXXXXBXXXXCXXXXX"));
        var abc = Assert.Single(results, r => r.Word == "ABC");
        Assert.Equal(new[] { 0, 5, 10 }, abc.Path);
    }

    [Fact]
    public void RejectsNonAdjacentLetters()
    {
        var solver = TrieSolver.LoadFromWords(new[] { "ABE" });
        // A is at (0,0), B at (0,2) — not adjacent, E at (0,3).
        var results = solver.Solve(Grid("AXBEXXXXXXXXXXXX"));
        Assert.Empty(results);
    }

    [Fact]
    public void NeverReusesATile()
    {
        var solver = TrieSolver.LoadFromWords(new[] { "AAA" });
        // Only two As on the board — AAA must not be found by revisiting one.
        var results = solver.Solve(Grid("AAXXXXXXXXXXXXXX"));
        Assert.Empty(results);
    }

    [Fact]
    public void EnforcesMinimumWordLength()
    {
        var solver = TrieSolver.LoadFromWords(new[] { "AXE", "AXES" });
        var four = solver.Solve(Grid("AXESXXXXXXXXXXXX"), minWordLength: 4)
            .Select(r => r.Word).ToList();
        Assert.Equal(new[] { "AXES" }, four);
    }

    [Fact]
    public void SkipsUnknownCells()
    {
        var solver = TrieSolver.LoadFromWords(new[] { "CAT" });
        var letters = Grid("CATSXXXXXXXXXXXX");
        letters[1] = '\0'; // the A failed OCR
        var results = solver.Solve(letters);
        Assert.Empty(results);
    }

    [Fact]
    public void SortsByScoreThenLength()
    {
        // QI would score high per letter; use realistic words:
        // "JO" too short; craft: XU (no)… keep abstract:
        var solver = TrieSolver.LoadFromWords(new[] { "TEE", "TEEN", "QAT" });
        // Q A T E
        // . . E N
        // . . . .
        // . . . .
        var results = solver.Solve(Grid("QATEXXENXXXXXXXX"));

        // QAT = 10+1+1 = 12, TEEN = 1+1+1+1 = 4, TEE = 3.
        Assert.Equal(new[] { "QAT", "TEEN", "TEE" }, results.Select(r => r.Word));
        Assert.Equal(12, results[0].Score);
    }

    [Fact]
    public void PrefersLongerWordOnScoreTie()
    {
        // HAT = 4+1+1 = 6 and DEAD = 2+1+1+2 = 6: a genuine score tie.
        var solver = TrieSolver.LoadFromWords(new[] { "HAT", "DEAD" });
        // H A T X
        // D E A D
        var results = solver.Solve(Grid("HATXDEADXXXXXXXX"));

        Assert.Equal(2, results.Count);
        Assert.Equal(results[0].Score, results[1].Score);
        Assert.Equal("DEAD", results[0].Word); // longer word wins the tie
    }

    [Fact]
    public void PathsAreValidAdjacentUniqueAndSpellTheWord()
    {
        var solver = TrieSolver.LoadFromWords(new[]
        {
            "MISER", "SCREAM", "CREAM", "RECANT", "TRANCE", "NECTAR", "CANE", "RACE",
        });
        // M I S C
        // T N A R
        // X X E X
        // X X X X
        var letters = "MISCTNARXXEXXXXX".ToCharArray();
        var results = solver.Solve(letters);
        Assert.NotEmpty(results);

        foreach (var result in results)
        {
            // Path spells the word.
            var spelled = new string(result.Path.Select(i => letters[i]).ToArray());
            Assert.Equal(result.Word, spelled);

            // Tiles are unique.
            Assert.Equal(result.Path.Count, result.Path.Distinct().Count());

            // Consecutive tiles are 8-adjacent.
            for (int i = 1; i < result.Path.Count; i++)
            {
                int a = result.Path[i - 1], b = result.Path[i];
                int dr = Math.Abs(a / 4 - b / 4), dc = Math.Abs(a % 4 - b % 4);
                Assert.True(dr <= 1 && dc <= 1 && (dr + dc) > 0,
                    $"{result.Word}: cells {a}->{b} not adjacent");
            }
        }
    }

    [Fact]
    public void DeduplicatesWordsFoundViaMultiplePaths()
    {
        var solver = TrieSolver.LoadFromWords(new[] { "TAT" });
        // T A T T — TAT reachable several ways; must appear once.
        var results = solver.Solve(Grid("TATTXXXXXXXXXXXX"));
        Assert.Single(results);
    }

    [Fact]
    public void FiltersDictionaryToBoardCompatibleWords()
    {
        var solver = TrieSolver.LoadFromWords(new[]
        {
            "AB",                  // too short
            "ANTIDISESTABLISH",    // 16 letters — allowed
            "ANTIDISESTABLISHX",   // 17 letters — filtered
            "CAT'S",               // apostrophe — filtered
            "NAÏVE",               // non A-Z — filtered
            "dog",                 // lowercase — normalized and kept
        });
        Assert.Equal(2, solver.WordCount); // ANTIDISESTABLISH + DOG

        var results = solver.Solve(Grid("DOGXXXXXXXXXXXXX"));
        Assert.Contains(results, r => r.Word == "DOG");
    }

    [Fact]
    public void SolvesFullSowpodsBoardQuickly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "sowpods.txt");
        var solver = TrieSolver.LoadFromFile(path);
        Assert.True(solver.WordCount > 200_000, $"expected >200k words, got {solver.WordCount}");

        // Board from the reference screenshot (reading order):
        // E A N V / E R C T / Y S U A / F M I V
        // MISCREANT: M(3,1) I(3,2) S(2,1) C(1,2) R(1,1) E(0,0) A(0,1) N(0,2) T(1,3)
        var letters = "EANVERCTYSUAFMIV".ToCharArray();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = solver.Solve(letters);
        sw.Stop();

        Assert.Contains(results, r => r.Word == "MISCREANT");
        Assert.True(results.Count > 50, $"expected a rich board, got {results.Count} words");
        // Spec target is <20ms; allow slack for CI noise.
        Assert.True(sw.ElapsedMilliseconds < 250, $"solve took {sw.ElapsedMilliseconds}ms");
    }
}
