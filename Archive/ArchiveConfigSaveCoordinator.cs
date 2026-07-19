using System;
using Arkadia.Data;

namespace Arkadia.Archive;

/// <summary>Result of an atomic archive-aware DAT-line config save.</summary>
public enum ArchiveConfigSaveOutcome
{
    /// <summary>Plan is valid (no collision, or resolved by exclusions) — config + validation were committed.</summary>
    Committed,
    /// <summary>An unresolved collision needs review before the config can be committed.</summary>
    NeedsReview,
}

/// <summary>
/// Encapsulates the atomic save decision for archive-aware DAT-line configuration,
/// so both the UI and tests share one code path (no reordering logic in the dialog).
///
/// Contract: validation runs against the IN-MEMORY selected options BEFORE any
/// strategy/mapping is persisted. Config + validation are persisted only once the
/// plan is valid. If review is aborted, the caller invokes <see cref="RollbackExclusions"/>
/// and persists nothing — leaving the DB exactly as it was before the save attempt.
/// </summary>
public sealed class ArchiveConfigSaveCoordinator
{
    private readonly DatLineArchiveOutputValidationService _validation;

    public ArchiveConfigSaveCoordinator(CatalogService catalog)
        => _validation = new DatLineArchiveOutputValidationService(catalog);

    /// <summary>
    /// If the session has no unresolved collision, persists the config (via
    /// <paramref name="persistConfig"/>) and the validation state, then returns
    /// <see cref="ArchiveConfigSaveOutcome.Committed"/>. Otherwise returns
    /// <see cref="ArchiveConfigSaveOutcome.NeedsReview"/> and persists nothing.
    /// </summary>
    public ArchiveConfigSaveOutcome TryCommit(
        string datLineId,
        ArchiveCollisionReviewSession session,
        Action persistConfig)
    {
        if (session.HasUnresolvedCollision)
            return ArchiveConfigSaveOutcome.NeedsReview;

        persistConfig();
        _validation.PersistResult(datLineId, session.Result);
        return ArchiveConfigSaveOutcome.Committed;
    }

    /// <summary>
    /// Rolls back every exclusion the review applied, restoring each release to its
    /// pre-review status. Used when the user aborts, so a cancelled review marks no
    /// release unwanted. Never deletes files.
    /// </summary>
    public static void RollbackExclusions(ArchiveCollisionReviewSession session, DatLineStore store)
    {
        foreach (var (releaseId, originalStatus) in session.ExcludedReleases)
        {
            // RestoreWantedRelease is the only exit from 'unwanted' (→ 'missing').
            store.RestoreWantedRelease(releaseId);
            // Then re-apply the exact pre-review status when it was not already 'missing'.
            if (originalStatus.Length > 0 &&
                !string.Equals(originalStatus, "missing",  StringComparison.Ordinal) &&
                !string.Equals(originalStatus, "unwanted", StringComparison.Ordinal))
                store.UpdateReleaseStatus(releaseId, originalStatus);
        }
    }
}
