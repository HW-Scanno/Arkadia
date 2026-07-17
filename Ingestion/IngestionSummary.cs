using System.Collections.Generic;

namespace Arkadia.Ingestion;

/// <summary>
/// Single source of truth for the ingestion counter set shown to the user.
/// Both the progress dialog summary and the final log render from
/// <see cref="CoreCounters"/>, so the two surfaces cannot drift apart.
/// This is presentation only — it never changes ingestion behavior.
/// </summary>
public static class IngestionSummary
{
    /// <summary>
    /// Ordered (label, value) counter set shared by the dialog summary and the
    /// final log. Values come straight from <see cref="IngestionResult"/> — no
    /// value is invented or derived unreliably.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> CoreCounters(IngestionResult r)
    {
        var list = new List<(string, string)>
        {
            ("Files scanned",             r.FilesScanned.ToString("N0")),
            ("Files matched",             r.FilesMatched.ToString("N0")),
            ("Files staged",              r.FilesCopied.ToString("N0")),
            ("Release inputs assembled",  r.ReleaseInputsAssembled.ToString("N0")),
            ("Derived artifacts created", r.DerivedArtifactsCreated.ToString("N0")),
            ("Already present",           r.AlreadyPresent.ToString("N0")),
            ("Releases present",          r.ReleasesPresent.ToString("N0")),
            ("Releases incomplete",       r.ReleasesIncomplete.ToString("N0")),
            ("Files skipped",             r.FilesSkipped.ToString("N0")),
            ("Unwanted skipped",          r.UnwantedSkipped.ToString("N0")),
            ("Transforms failed",         r.TransformsFailed.ToString("N0")),
            ("Archives deleted",          r.FilesDeletedFromIncoming.ToString("N0")),
        };

        // Stale-cleanup rows are shown only when they actually occurred, so normal
        // runs keep the standard 12-counter summary unchanged.
        if (r.StaleStagingMoved > 0)
            list.Add(("Stale staging moved", r.StaleStagingMoved.ToString("N0")));
        if (r.StaleSourceMoved > 0)
            list.Add(("Stale source moved", r.StaleSourceMoved.ToString("N0")));

        return list;
    }

    /// <summary>
    /// True when the run produced any state change worth refreshing the UI for —
    /// including an all-unwanted run (only <see cref="IngestionResult.UnwantedSkipped"/> &gt; 0),
    /// which still changed <c>incoming-skip</c>.
    /// </summary>
    public static bool ShouldRefreshAfterIngest(IngestionResult r)
        => r.Error is null &&
           (r.FilesCopied      > 0 ||
            r.FilesSkipped      > 0 ||
            r.ReleasesPresent   > 0 ||
            r.UnwantedSkipped   > 0);

    /// <summary>
    /// Optional clarifying note for a successful run that acquired no wanted
    /// releases but moved unwanted files aside. Null when not applicable.
    /// </summary>
    public static string? AllUnwantedNote(IngestionResult r)
        => r.Error is null && r.ReleasesPresent == 0 && r.UnwantedSkipped > 0
            ? $"Ingestion completed: no wanted releases acquired; " +
              $"{r.UnwantedSkipped:N0} unwanted file(s) moved to incoming-skip."
            : null;
}
