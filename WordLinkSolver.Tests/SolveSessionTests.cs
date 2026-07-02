using WordLinkSolver.Core;
using WordLinkSolver.Models;
using Xunit;

namespace WordLinkSolver.Tests;

public class SolveSessionTests
{
    private static List<WordResult> Words(params (string Word, int Score)[] items) =>
        items.Select(i => new WordResult(i.Word, new[] { 0 }, i.Score)).ToList();

    private static SolveSession NewSession(params (string, int)[] items)
    {
        var session = new SolveSession();
        session.SetResults(Words(items));
        return session;
    }

    [Fact]
    public void CurrentIsTheBestWord()
    {
        var session = NewSession(("ALPHA", 20), ("BETA", 10));
        Assert.Equal("ALPHA", session.Current?.Word);
    }

    [Fact]
    public void CurrentIsNullWithoutResults()
    {
        var session = new SolveSession();
        Assert.Null(session.Current);
        Assert.False(session.HasResults);
    }

    [Fact]
    public void RerollCyclesThroughTopWordsAndWraps()
    {
        var session = NewSession(("A", 30), ("B", 20), ("C", 10));

        session.Reroll();
        Assert.Equal("B", session.Current?.Word);
        session.Reroll();
        Assert.Equal("C", session.Current?.Word);
        session.Reroll();
        Assert.Equal("A", session.Current?.Word); // wrapped
    }

    [Fact]
    public void RerollIsBoundedByTheTopTenWindow()
    {
        var items = Enumerable.Range(0, 15).Select(i => ($"W{i:D2}", 100 - i)).ToArray();
        var session = NewSession(items);

        for (int i = 0; i < SolveSession.RerollWindow; i++)
            session.Reroll();

        // After exactly RerollWindow rerolls we are back at the best word.
        Assert.Equal("W00", session.Current?.Word);
    }

    [Fact]
    public void BanRemovesTheCurrentWordAndShowsNextBest()
    {
        var session = NewSession(("TOP", 30), ("NEXT", 20), ("LAST", 10));

        Assert.True(session.BanCurrent());
        Assert.Equal("NEXT", session.Current?.Word);
        Assert.DoesNotContain(session.Candidates, w => w.Word == "TOP");
    }

    [Fact]
    public void BanSurvivesNewResultsUntilCleared()
    {
        var session = NewSession(("TOP", 30), ("NEXT", 20));
        session.BanCurrent();

        // A re-solve of the same board keeps the ban…
        session.SetResults(Words(("TOP", 30), ("NEXT", 20)));
        Assert.Equal("NEXT", session.Current?.Word);

        // …until Fresh Scan clears it.
        session.ClearBans();
        Assert.Equal("TOP", session.Current?.Word);
    }

    [Fact]
    public void BanningEverythingLeavesNoCurrentWord()
    {
        var session = NewSession(("ONLY", 5));
        Assert.True(session.BanCurrent());
        Assert.Null(session.Current);
        Assert.False(session.BanCurrent());
        Assert.True(session.HasResults); // solve happened; just nothing left
    }

    [Fact]
    public void RerollResetsAfterBan()
    {
        var session = NewSession(("A", 30), ("B", 20), ("C", 10));
        session.Reroll(); // → B
        session.BanCurrent(); // ban B → back to best remaining
        Assert.Equal("A", session.Current?.Word);
    }

    [Fact]
    public void TopReturnsAtMostNWithoutBannedWords()
    {
        var session = NewSession(("A", 30), ("B", 20), ("C", 10));
        session.BanCurrent(); // ban A
        var top = session.Top(2);
        Assert.Equal(new[] { "B", "C" }, top.Select(w => w.Word));
    }
}
