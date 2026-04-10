using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Data;

/// <summary>
/// Computes the reconciliation diff when a DAT line is updated with a new parsed game set.
/// </summary>
public static class ReconciliationEngine
{
    /// <summary>
    /// Whether a pre-update release status represents physically owned content
    /// that can be realigned to a new release without acquiring anything new.
    /// </summary>
    private static bool IsReusable(string preUpdateStatus)
        => preUpdateStatus == "present";

    /// <summary>
    /// Applies a DAT update to an existing DAT-line DB.
    ///
    /// Algorithm:
    /// 1. Load the existing release set from the store.
    /// 2. Partition into: kept (name still present in new DAT), removed (name gone).
    /// 3. Mark removed releases OUTDATED (status written as "outdated").
    /// 4. For each new game name not in the old set (introduced):
    ///    - Search only among removed releases whose PRE-UPDATE status was physically
    ///      reusable (currently: "present" only).
    ///    - Require exact non-empty ContentKey equality AND a unique match (count == 1).
    ///    - If exactly one reusable match: new release → PENDING, insert pending row.
    ///    - Otherwise (zero matches, ambiguous, or all matches non-reusable): → MISSING.
    ///    A prior MISSING or LOST release is never reusable and must never produce PENDING.
    /// 5. Kept releases retain their current status; their ContentKey is updated if the
    ///    old value was empty and the new DAT now provides one.
    /// 6. Persist the full resulting release set via SaveReleases.
    /// </summary>
    /// <param name="store">Open DAT-line store to read from and write to.</param>
    /// <param name="datLineId">DAT line identifier — set on newly created release rows.</param>
    /// <param name="newGames">Parsed game list from the updated DAT file.</param>
    /// <returns>Summary counts of the reconciliation pass.</returns>
    public static ReconciliationResult ApplyDatUpdate(
        DatLineStore                store,
        string                      datLineId,
        List<DatParser.ParsedGame>  newGames)
    {
        var existing = store.LoadReleases();

        // Index existing releases by name for O(1) lookup.
        var existingByName = existing.ToDictionary(r => r.Name, StringComparer.Ordinal);

        // Build the new-name set for fast containment check.
        var newNameSet = newGames.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);

        // ── Partition existing releases ───────────────────────────────────────

        // Kept: name still appears in the new DAT.
        // Removed: name has disappeared → will be marked OUTDATED.
        var kept    = new List<ReleaseRecord>();
        var removed = new List<ReleaseRecord>();

        foreach (var r in existing)
        {
            if (newNameSet.Contains(r.Name))
                kept.Add(r);
            else
                removed.Add(r);
        }

        // Capture pre-update status before mutating, then mark removed as OUTDATED.
        // Pre-update status is what determines reusability for matching.
        var preUpdateStatus = removed.ToDictionary(r => r.Id, r => r.Status, StringComparer.Ordinal);
        foreach (var r in removed)
            r.Status = "outdated";

        // Update ReleaseContentKey on kept releases when previously empty.
        var newGameByName = newGames.ToDictionary(g => g.Name, StringComparer.Ordinal);
        foreach (var r in kept)
        {
            if (r.ReleaseContentKey.Length == 0 && newGameByName.TryGetValue(r.Name, out var g))
                r.ReleaseContentKey = g.ContentKey;
        }

        // ── Build indexes for matching ────────────────────────────────────────
        // Ambiguity is checked across ALL removed releases for a given ReleaseContentKey,
        // regardless of reusability.  Two prior releases that share the same content
        // identity — even if only one is present — constitute an ambiguous mapping.
        // Only when exactly one removed release holds a ReleaseContentKey AND that release
        // was physically owned (pre-update status reusable) do we create PENDING.
        var allRemovedByKey = removed
            .Where(r => r.ReleaseContentKey.Length > 0)
            .GroupBy(r => r.ReleaseContentKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // ── Process introduced releases ───────────────────────────────────────

        var introduced  = new List<ReleaseRecord>();
        var pendingRows = new List<PendingReconciliationRecord>();

        foreach (var game in newGames)
        {
            if (existingByName.ContainsKey(game.Name))
                continue;   // already handled in kept set

            string status;
            PendingReconciliationRecord? pendingRow = null;

            if (game.ContentKey.Length > 0
                && allRemovedByKey.TryGetValue(game.ContentKey, out var candidates)
                && candidates.Count == 1
                && preUpdateStatus.TryGetValue(candidates[0].Id, out var priorStatus)
                && IsReusable(priorStatus))
            {
                // Exactly one removed release maps to this content key, and it was
                // physically owned before this update — safe to propose realignment.
                var newId = Guid.NewGuid().ToString("N");
                status = "pending";

                pendingRow = new PendingReconciliationRecord
                {
                    Id                = Guid.NewGuid().ToString("N"),
                    NewReleaseId      = newId,
                    OutdatedReleaseId = candidates[0].Id,
                    TargetName        = game.Name,
                    Reason            = "content_hash_match",
                    CreatedAtUtc      = DateTime.UtcNow,
                    Status            = "pending",
                };

                introduced.Add(new ReleaseRecord
                {
                    Id               = newId,
                    DatLineId        = datLineId,
                    Name             = game.Name,
                    Status           = status,
                    Region           = game.Region,
                    Languages        = game.Languages,
                    ReleaseContentKey = game.ContentKey,
                    IntroducedAtUtc  = DateTime.UtcNow,
                });
                pendingRows.Add(pendingRow);
            }
            else
            {
                // Zero reusable matches, ambiguous matches, or prior content not
                // physically owned (missing/lost) — new release stays MISSING.
                introduced.Add(new ReleaseRecord
                {
                    Id               = Guid.NewGuid().ToString("N"),
                    DatLineId        = datLineId,
                    Name             = game.Name,
                    Status           = "missing",
                    Region           = game.Region,
                    Languages        = game.Languages,
                    ReleaseContentKey = game.ContentKey,
                    IntroducedAtUtc  = DateTime.UtcNow,
                });
            }
        }

        // ── Persist ───────────────────────────────────────────────────────────

        var fullSet = new List<ReleaseRecord>(kept.Count + removed.Count + introduced.Count);
        fullSet.AddRange(kept);
        fullSet.AddRange(removed);
        fullSet.AddRange(introduced);

        store.SaveReleases(fullSet);

        foreach (var row in pendingRows)
            store.SavePendingReconciliation(row);

        return new ReconciliationResult
        {
            Kept     = kept.Count,
            Outdated = removed.Count,
            Pending  = introduced.Count(r => r.Status == "pending"),
            Missing  = introduced.Count(r => r.Status == "missing"),
        };
    }
}

/// <summary>Summary of a single reconciliation pass.</summary>
public sealed record ReconciliationResult
{
    /// <summary>Releases present in both old and new DAT — status preserved.</summary>
    public int Kept     { get; init; }
    /// <summary>Releases removed from the new DAT — marked OUTDATED.</summary>
    public int Outdated { get; init; }
    /// <summary>New releases with exactly one unique content-key match — marked PENDING.</summary>
    public int Pending  { get; init; }
    /// <summary>New releases with zero or ambiguous content-key matches — marked MISSING.</summary>
    public int Missing  { get; init; }
}
