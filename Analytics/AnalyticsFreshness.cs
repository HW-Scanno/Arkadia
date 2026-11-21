namespace Arkadia.Analytics;

/// <summary>
/// Tracks whether the collected Analytics data is behind the databases. UI-free.
///
/// <para>The Analytics view recomputes everything each time it is opened, so a mutation made while the view is
/// hidden does not need an immediate (all-DAT-lines) recalculation: it only has to mark the data stale. A
/// mutation made while the view is visible must recalculate at once, because nothing else would refresh what the
/// user is looking at.</para>
/// </summary>
public sealed class AnalyticsFreshness
{
    /// <summary>True when a mutation happened since the last build and the data has not been rebuilt yet.</summary>
    public bool IsStale { get; private set; }

    /// <summary>
    /// Records a mutation that changes what Analytics would show. Returns true when the caller must rebuild
    /// NOW (the view is visible); otherwise the data is marked stale and rebuilt on the next open/use.
    /// </summary>
    public bool Invalidate(bool viewVisible)
    {
        if (viewVisible) return true;
        IsStale = true;
        return false;
    }

    /// <summary>Call when a build completes: the data reflects the databases again.</summary>
    public void MarkBuilt() => IsStale = false;
}
