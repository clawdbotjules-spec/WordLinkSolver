using WordLinkSolver.Models;

namespace WordLinkSolver.Core;

/// <summary>
/// Holds the results of the latest solve plus the transient user state around
/// them: the ban list (B key) and the reroll cursor (R key).
///
/// The solver returns every word on the board sorted best-first; banning a word
/// simply removes it from the candidate view (equivalent to re-solving with the
/// word excluded, but instant), and rerolling cycles through the top
/// <see cref="RerollWindow"/> candidates.
/// </summary>
public sealed class SolveSession
{
    /// <summary>R cycles through this many top words.</summary>
    public const int RerollWindow = 10;

    private List<WordResult> _all = new();
    private readonly HashSet<string> _banned = new(StringComparer.OrdinalIgnoreCase);
    private List<WordResult>? _candidatesCache;
    private int _rerollIndex;

    /// <summary>True once a solve has been stored (even if it found nothing).</summary>
    public bool HasResults { get; private set; }

    /// <summary>Words currently banned via the B key.</summary>
    public IReadOnlyCollection<string> BannedWords => _banned;

    /// <summary>
    /// Stores fresh solver output. Bans persist (they only reset on Fresh Scan);
    /// the reroll cursor goes back to the best word.
    /// </summary>
    public void SetResults(List<WordResult> results)
    {
        _all = results;
        _rerollIndex = 0;
        _candidatesCache = null;
        HasResults = true;
    }

    /// <summary>All non-banned words, best first.</summary>
    public IReadOnlyList<WordResult> Candidates
    {
        get
        {
            _candidatesCache ??= _banned.Count == 0
                ? _all
                : _all.Where(w => !_banned.Contains(w.Word)).ToList();
            return _candidatesCache;
        }
    }

    /// <summary>The word currently shown to the user, or null when none remain.</summary>
    public WordResult? Current
    {
        get
        {
            var c = Candidates;
            if (c.Count == 0)
                return null;
            return c[Math.Min(_rerollIndex, Math.Min(c.Count, RerollWindow) - 1)];
        }
    }

    /// <summary>Top <paramref name="n"/> non-banned words for display.</summary>
    public IReadOnlyList<WordResult> Top(int n) => Candidates.Take(n).ToList();

    /// <summary>
    /// Bans the currently displayed word and moves to the next best.
    /// Returns false when there is nothing to ban.
    /// </summary>
    public bool BanCurrent()
    {
        var current = Current;
        if (current is null)
            return false;
        _banned.Add(current.Word);
        _candidatesCache = null;
        _rerollIndex = 0;
        return true;
    }

    /// <summary>Cycles to the next of the top candidates (wraps around).</summary>
    public void Reroll()
    {
        int window = Math.Min(Candidates.Count, RerollWindow);
        if (window <= 1)
            return;
        _rerollIndex = (_rerollIndex + 1) % window;
    }

    /// <summary>Clears the ban list (Fresh Scan).</summary>
    public void ClearBans()
    {
        if (_banned.Count == 0)
            return;
        _banned.Clear();
        _candidatesCache = null;
        _rerollIndex = 0;
    }

    /// <summary>Drops everything, e.g. when the board could not be read.</summary>
    public void Reset()
    {
        _all = new List<WordResult>();
        _banned.Clear();
        _candidatesCache = null;
        _rerollIndex = 0;
        HasResults = false;
    }
}
