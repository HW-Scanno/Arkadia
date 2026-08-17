using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// Group Verify Volume orchestrator (V-G5). A Group volume is verified leaf-by-leaf by REUSING the exact Single
/// engine (<see cref="VolumeVerifyService.VerifyCore"/>) — never a Group-native verifier. For each assigned leaf
/// it supplies that leaf's scoped assignments, that leaf's <see cref="DatLineStore"/>, and the leaf physical root
/// <c>&lt;groupRoot&gt;/&lt;leaf-segment&gt;</c>, so the flat classification/recovery logic runs correctly inside
/// the leaf and sibling leaves are invisible. The owning <c>volumeId</c> stays the Group volume id throughout
/// (size decrement / self-exclusion operate on the real owner). Runs are sequential and deterministic
/// (DatLineId Ordinal); reachability is the GROUP root's existence for every leaf. Mapping/membership is fully
/// validated BEFORE the first leaf runs — any drift BLOCKS with no mutation. A leaf exception propagates (STOP);
/// earlier leaves' committed recovery/DB changes remain, matching Single partial-progress semantics.
/// </summary>
public static class GroupVolumeVerifyOrchestrator
{
    /// <summary>Aggregated outcome, or a pre-flight <see cref="BlockReason"/> (no leaf ran, nothing mutated).</summary>
    public sealed record Outcome(VolumeVerifyResult? Result, string? BlockReason)
    {
        public static Outcome Blocked(string reason) => new(null, reason);
    }

    public static Outcome Verify(
        CatalogService                    catalog,
        string                            dataDir,
        VolumeRecord                      volume,
        string                            groupRoot,
        IReadOnlyList<GroupLeafRecord>    groupLeaves,
        IReadOnlyList<string>             allDatLineDbPaths,
        IProgress<FoundFileProgress>?     foundFileProgress = null,
        IProgress<VolumeVerifyProgress>?  verifyProgress    = null,
        IProgress<string>?                leafProgress      = null)
    {
        if (volume.GroupId is not { } groupId)
            return Outcome.Blocked("Internal error: Group Verify requested for a non-Group volume.");

        var vas = catalog.GetVolumeArtifacts(volume.Id);

        // Authoritative leaf ids come from the assignment rows (never current navigation state); deterministic order.
        var assignedLeafIds = vas.Select(v => v.DatLineId).Distinct(StringComparer.Ordinal)
                                 .OrderBy(id => id, StringComparer.Ordinal).ToList();

        // Revalidate the leaf → physical-segment mapping (empty/unsafe/collision) for exactly those leaves.
        var (segmentMap, segErr) = GroupRelativeLeafPath.BuildMap(groupId, assignedLeafIds);
        if (segErr is not null)
            return Outcome.Blocked($"Cannot verify this Group volume — {segErr}");

        // Resolve every assigned leaf against current membership + DB availability BEFORE any leaf verify runs.
        var membershipById = groupLeaves.ToDictionary(l => l.DatLine.Id, StringComparer.Ordinal);
        var plan           = new List<(string LeafId, string Segment, string DbPath)>();
        foreach (var leafId in assignedLeafIds)
        {
            if (!membershipById.TryGetValue(leafId, out var leaf))
                return Outcome.Blocked(
                    $"Assigned leaf '{leafId}' is no longer a member of group '{groupId}'. " +
                    "Reconcile the group before verifying this volume. No files were changed.");

            var dsp = leaf.DatLine.DataStorePath;
            var abs = dsp.Length > 0 ? Path.Combine(dataDir, dsp) : "";
            if (abs.Length == 0 || !File.Exists(abs))
                return Outcome.Blocked(
                    $"The database for leaf '{leafId}' is unavailable, so it cannot be verified. " +
                    "No files were changed.");

            plan.Add((leafId, segmentMap[leafId], abs));
        }

        // Group-root reachability drives per-leaf reconciliation: a missing leaf subfolder under a reachable
        // Group root is genuinely-missing content, not an offline volume.
        bool groupReachable = Directory.Exists(groupRoot);

        var svc     = new VolumeVerifyService(catalog);
        var results = new List<VolumeVerifyResult>(plan.Count);

        foreach (var (leafId, segment, leafDbPath) in plan)   // DatLineId Ordinal (plan preserves assignedLeafIds order)
        {
            leafProgress?.Report(leafId);

            var scoped    = vas.Where(v => v.DatLineId == leafId).ToList();
            var leafRoot  = Path.Combine(groupRoot, segment);
            var leafStore = new DatLineStore(leafDbPath);   // writable — VerifyCore updates DA/release status

            // A leaf exception propagates (STOP). Earlier leaves' committed changes remain (no rollback/journal).
            var result = svc.VerifyCore(
                volume.Id, scoped, leafRoot, leafStore, allDatLineDbPaths,
                foundFileProgress, verifyProgress, reachableOverride: groupReachable);

            results.Add(result);
        }

        return new Outcome(VolumeVerifyResult.Combine(results), null);
    }
}
