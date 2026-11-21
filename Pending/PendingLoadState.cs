namespace Arkadia.Pending;

/// <summary>What the caller must do with a finished Pending scan.</summary>
public enum PendingLoadOutcome
{
    /// <summary>The result is current: show it. The view is ready.</summary>
    Ready,
    /// <summary>The data changed while the scan ran: discard the result (never show it); scan again when the view needs it.</summary>
    Stale,
    /// <summary>The scan failed and nothing changed meanwhile: show the failure; the next request scans again.</summary>
    Failed,
}

/// <summary>
/// Gate of the on-demand, off-thread Pending scan. UI-free; every member is called on the UI thread only — the scan
/// itself is the only thing that runs elsewhere, and it reports back through <see cref="Complete"/> on the UI thread.
///
/// <para><b>Single flight.</b> At most one scan runs: <see cref="TryBegin"/> refuses while one is in progress, and
/// while the shown data is current.</para>
///
/// <para><b>Never stale.</b> Every change of the pending reconciliations calls <see cref="Invalidate"/>, which makes
/// the shown data not ready and makes any scan started before it obsolete: its result is reported
/// <see cref="PendingLoadOutcome.Stale"/> and must be dropped, so a scan that read the databases before (or while) an
/// operation changed them can never be presented as ready.</para>
/// </summary>
public sealed class PendingLoadState
{
    private int _generation;   // bumped by every invalidation; a scan is current only for the generation it started in

    /// <summary>True when the last applied scan still reflects the databases.</summary>
    public bool IsReady { get; private set; }

    /// <summary>True while a scan is running.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>The pending reconciliations changed (or may have): what is shown is no longer current.</summary>
    public void Invalidate()
    {
        _generation++;
        IsReady = false;
    }

    /// <summary>
    /// Asks to start a scan. False when the shown data is current or a scan is already running (nothing to start).
    /// On true the caller MUST run one scan and report it with <see cref="Complete"/>, passing <paramref name="token"/> back.
    /// </summary>
    public bool TryBegin(out int token)
    {
        token = _generation;
        if (IsReady || IsLoading) return false;
        IsLoading = true;
        return true;
    }

    /// <summary>Reports the end of the scan started with <paramref name="token"/>.</summary>
    public PendingLoadOutcome Complete(int token, bool succeeded)
    {
        IsLoading = false;
        if (token != _generation) return PendingLoadOutcome.Stale;
        if (!succeeded) return PendingLoadOutcome.Failed;
        IsReady = true;
        return PendingLoadOutcome.Ready;
    }
}
