namespace Arkadia.Volumes;

/// <summary>
/// Pure presentation mapping for the <b>Group</b> Append preview (V-G6 micro-fix). The Append dialog and its plan
/// hints are authored for Single volumes ("DAT line"); a Group volume owns a <i>collection</i> of leaves, so the
/// Group preview needs collection terminology and must always identify the collection — including for an empty
/// (0-planned) plan. Keeping this pure lets it be unit-tested without an Avalonia UI harness. Single presentation
/// is untouched: the dialog only calls this on the Group path.
/// </summary>
public static class GroupAppendPresentation
{
    /// <summary>Rewrites Single-authored hint wording for a Group owner: "DAT line" → "collection".</summary>
    public static string Groupify(string hint) => hint.Replace("DAT line", "collection");

    /// <summary>
    /// The final Group plan-hint line: the always-visible collection identity (independent of CanExecute /
    /// PlannedCount) followed by the collection-aware base hint. When <paramref name="baseHint"/> is empty only
    /// the identity is shown.
    /// </summary>
    public static string ComposeHint(string collectionDisplay, int totalLeaves, string baseHint)
    {
        var identity = $"Collection: {collectionDisplay}  ·  {totalLeaves:N0} leaf DAT(s)";
        var hint     = Groupify(baseHint);
        return hint.Length > 0 ? $"{identity}  —  {hint}" : identity;
    }
}
