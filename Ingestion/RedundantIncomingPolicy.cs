namespace Arkadia.Ingestion;

/// <summary>DB-authoritative staging-admission satisfaction for a release.</summary>
internal enum ReleaseSatisfaction
{
    /// <summary>Not satisfied — incoming material may be needed; stage normally.
    /// Covers every status other than <c>present</c> (missing / lost / pending / no derived artifact).</summary>
    NotSatisfied,

    /// <summary>Satisfied — the DB says the release is <c>present</c> AND at least one
    /// <c>derived_artifacts</c> row exists. The durable artifact is trusted from the DB
    /// regardless of where it is recorded (local archive, reachable volume, offline volume,
    /// legacy relative_path). Incoming constituents are redundant and must not create staging.</summary>
    Satisfied,

    /// <summary>Inconsistent DB state — status is <c>present</c> but NO <c>derived_artifacts</c>
    /// row exists. The release looks present but ingestion has no durable artifact record to
    /// trust, so it is NOT satisfied for staging admission (it may be staged so a real artifact
    /// can be produced) and the case is logged for visibility.</summary>
    PresentWithoutArtifact,
}

/// <summary>
/// Pre-staging admission guard — decides whether re-ingesting a release's constituent
/// source files (e.g. a lone <c>.cue</c> for a CD release whose CHD already exists) is
/// REDUNDANT and must not create a new staging folder.
///
/// <para><b>DB-authoritative (product decision).</b> Ingestion trusts the database for
/// staging admission and does NOT probe the filesystem to decide whether incoming material
/// should be staged. A release is satisfied when the DB says it is <c>present</c> AND has at
/// least one <c>derived_artifacts</c> row — regardless of where that artifact is currently
/// recorded (local archive, reachable volume, <b>offline assigned volume</b>, legacy
/// relative_path, or any other DB-tracked durable location).</para>
///
/// <para>Physical existence/integrity is deliberately out of scope here. If the DB says a
/// release is present but the file is actually missing or corrupt (disk failure, manual
/// deletion, filesystem drift), that is discovered and reconciled by <b>Verify Archive</b> /
/// <b>Verify Volume</b> — not by normal ingestion. Ingestion is not an integrity-verification
/// workflow.</para>
/// </summary>
internal static class RedundantIncomingPolicy
{
    /// <param name="releaseStatus">Release.Status as loaded from the DB at run start.</param>
    /// <param name="derivedArtifactRowCount">Number of <c>derived_artifacts</c> rows for the release.</param>
    internal static ReleaseSatisfaction Locate(string releaseStatus, int derivedArtifactRowCount)
    {
        if (releaseStatus != "present")
            return ReleaseSatisfaction.NotSatisfied;

        return derivedArtifactRowCount > 0
            ? ReleaseSatisfaction.Satisfied
            : ReleaseSatisfaction.PresentWithoutArtifact;
    }
}
