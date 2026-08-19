using System.Collections.Generic;

namespace Arkadia.Systems;

/// <summary>
/// The physical archive lifecycle of a collection's owned artifacts. The three named states are MUTUALLY
/// EXCLUSIVE; <see cref="None"/> means "no owned material yet — show no lifecycle badge". STAGING is deliberately
/// NOT a member here: it is a separate attention flag (see <see cref="StorageLifecycle"/>) that may appear
/// alongside any lifecycle state.
/// </summary>
public enum ArchiveLifecycle
{
    /// <summary>No owned present-derived artifacts and nothing unassigned — no lifecycle badge.</summary>
    None,
    /// <summary>Present derived artifacts exist in the Local Archive that are not assigned to any volume.</summary>
    NeedsVolume,
    /// <summary>All owned artifacts are assigned, but at least one relevant volume is still LOCAL (workspace).</summary>
    ReadyToMove,
    /// <summary>All owned artifacts are assigned and no relevant volume remains LOCAL — physically archived.</summary>
    Archived,
}

/// <summary>
/// Pure derivation of the archive lifecycle from a scope's storage facts, and the parent fold that rolls child
/// lifecycles up to a System/collection. STAGING is intentionally excluded — it is an independent boolean the
/// caller renders separately, so a card can show e.g. STAGING + ARCHIVED at once. No I/O, no Avalonia — the whole
/// badge policy is unit-testable in isolation.
/// </summary>
public static class StorageLifecycle
{
    /// <summary>
    /// Lifecycle for one collection. Precedence: NEEDS VOLUME &gt; READY TO MOVE &gt; ARCHIVED. Unassigned wins
    /// even when a LOCAL volume also exists, because finding a volume for the still-unassigned artifacts is the
    /// earlier unfinished step. With nothing unassigned and no owned material, the result is <see cref="None"/> —
    /// an untouched/empty DAT never reads ARCHIVED.
    /// </summary>
    /// <param name="ownedPresentDerived">Count of present derived artifacts owned by this scope.</param>
    /// <param name="unassigned">Present derived artifacts not assigned to any volume.</param>
    /// <param name="anyLocalVolume">True if any relevant volume's current location is the local workspace.</param>
    public static ArchiveLifecycle Derive(int ownedPresentDerived, int unassigned, bool anyLocalVolume)
    {
        if (unassigned > 0)          return ArchiveLifecycle.NeedsVolume;   // earliest unfinished step wins
        if (ownedPresentDerived <= 0) return ArchiveLifecycle.None;         // nothing owned & nothing unassigned
        return anyLocalVolume ? ArchiveLifecycle.ReadyToMove : ArchiveLifecycle.Archived;
    }

    /// <summary>
    /// Parent lifecycle from its children's lifecycles. Same precedence: any NEEDS VOLUME wins; else any READY TO
    /// MOVE; else any ARCHIVED (which already implies some child has owned, archived material); else None. A
    /// <see cref="None"/> child (no owned material) contributes nothing. Provably equivalent to deriving the
    /// parent from summed facts, but cheaper — a single fold over already-computed child states.
    /// </summary>
    public static ArchiveLifecycle Fold(IEnumerable<ArchiveLifecycle> children)
    {
        bool anyReadyToMove = false, anyArchived = false;
        foreach (var c in children)
        {
            if (c == ArchiveLifecycle.NeedsVolume) return ArchiveLifecycle.NeedsVolume;
            if (c == ArchiveLifecycle.ReadyToMove) anyReadyToMove = true;
            else if (c == ArchiveLifecycle.Archived) anyArchived = true;
        }
        if (anyReadyToMove) return ArchiveLifecycle.ReadyToMove;
        if (anyArchived)    return ArchiveLifecycle.Archived;
        return ArchiveLifecycle.None;
    }
}
