using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// Aggregated Group Append plan (V-G6): one per-leaf <see cref="AppendVolumePlan"/> for execution plus a synthetic
/// <see cref="Aggregate"/> plan for the single reused preview. On a pre-flight failure only <see cref="BlockReason"/>
/// is set (no leaf plan is produced, nothing is executed).
/// </summary>
public sealed class GroupAppendVolumePlan
{
    public required string                          VolumeId          { get; init; }
    public required string                          GroupId           { get; init; }
    public required string                          VolumeLabel       { get; init; }
    public required string                          CollectionDisplay { get; init; }
    /// <summary>Per-leaf plans in the deterministic DatLineId-Ordinal execution order (0-planned leaves included).</summary>
    public required IReadOnlyList<AppendVolumePlan> LeafPlans         { get; init; }
    /// <summary>Concatenated/summed view used to drive the existing single-plan preview dialog.</summary>
    public required AppendVolumePlan                Aggregate         { get; init; }
    public          string?                         BlockReason       { get; init; }

    public int LeavesInvolved => LeafPlans.Count(p => p.PlannedCount > 0);
}

/// <summary>
/// Group Append orchestrator/planner (V-G6). Planning is GLOBAL, execution is LEAF-SCOPED. It reuses the exact
/// Single first-fit core (<see cref="AppendVolumePlanner.PlanCore"/>) once per leaf, threading ONE remaining-capacity
/// budget across leaves in <b>DatLineId Ordinal</b> order (each leaf starts where the previous stopped), so the
/// whole Group volume gets a single deterministic packing decision — never independent per-leaf capacity. Each leaf
/// plan targets <c>&lt;groupRoot&gt;/&lt;leaf-segment&gt;/&lt;file&gt;</c> and writes the EXACT leaf DatLineId as the
/// artifact owner (never the GroupId). Reads only (leaf stores via <see cref="DatLineStore.OpenReadOnly"/>); mapping
/// and leaf-DB availability are validated before any execution.
/// </summary>
public static class GroupAppendPlanningService
{
    public static GroupAppendVolumePlan Plan(
        CatalogService                 catalog,
        string                         dataDir,
        string                         appRoot,
        VolumeRecord                   volume,
        string                         groupRoot,
        string                         collectionDisplay,
        IReadOnlyList<GroupLeafRecord> groupLeaves)
    {
        if (volume.GroupId is not { } groupId)
            return Blocked(volume, collectionDisplay, "Internal error: Group Append requested for a non-Group volume.");

        // All group leaves participate (Append adds NEW unassigned artifacts). Deterministic Ordinal order.
        var leafIds = groupLeaves.Select(l => l.DatLine.Id).Distinct(StringComparer.Ordinal)
                                 .OrderBy(id => id, StringComparer.Ordinal).ToList();

        var (segmentMap, segErr) = GroupRelativeLeafPath.BuildMap(groupId, leafIds);
        if (segErr is not null)
            return Blocked(volume, collectionDisplay, $"Cannot append to this Group volume — {segErr}");

        var byId = groupLeaves.ToDictionary(l => l.DatLine.Id, StringComparer.Ordinal);

        // Reachability is the GROUP root's existence; a leaf sub-folder that does not exist yet is not a
        // collision and is created at copy time.
        bool groupReachable = Directory.Exists(groupRoot);

        var planner   = new AppendVolumePlanner(catalog);
        long allocated = 0;   // the ONE global budget threaded across leaves
        var leafPlans = new List<AppendVolumePlan>(leafIds.Count);

        foreach (var leafId in leafIds)
        {
            var leaf = byId[leafId];
            var dsp  = leaf.DatLine.DataStorePath;
            var abs  = dsp.Length > 0 ? Path.Combine(dataDir, dsp) : "";
            if (abs.Length == 0 || !File.Exists(abs))
                return Blocked(volume, collectionDisplay,
                    $"The database for leaf '{leafId}' is unavailable, so it cannot be planned. No files were changed.");

            var segment  = segmentMap[leafId];
            var leafRoot = Path.Combine(groupRoot, segment);

            var store = DatLineStore.OpenReadOnly(abs);   // read-only planning
            var plan  = planner.PlanCore(
                volume.Id, volume.Label, leafId, leaf.DatLine.HardwareFamilyId,
                volume.PlannedSizeBytes, volume.ActualSizeBytes,
                leafRoot, appRoot, store,
                initialAllocatedBytes: allocated, out var finalAllocated,
                rootReachableOverride: groupReachable);

            allocated = finalAllocated;
            leafPlans.Add(plan);
        }

        return new GroupAppendVolumePlan
        {
            VolumeId          = volume.Id,
            GroupId           = groupId,
            VolumeLabel       = volume.Label,
            CollectionDisplay = collectionDisplay,
            LeafPlans         = leafPlans,
            Aggregate         = BuildAggregate(volume, groupRoot, leafPlans),
            BlockReason       = null,
        };
    }

    // ── Aggregate synthesis (display only; never executed) ──────────────────────

    private static AppendVolumePlan BuildAggregate(
        VolumeRecord volume, string groupRoot, IReadOnlyList<AppendVolumePlan> leafPlans)
    {
        long targetFree   = volume.PlannedSizeBytes - volume.ActualSizeBytes;
        long plannedBytes = leafPlans.Sum(p => p.PlannedBytes);
        int  plannedCount = leafPlans.Sum(p => p.PlannedCount);

        var entries = leafPlans.SelectMany(p => p.Entries).ToList();

        var skipCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in leafPlans)
            foreach (var (k, v) in p.SkipReasonCounts)
                skipCounts[k] = skipCounts.GetValueOrDefault(k) + v;

        var nonZeroSmallest = leafPlans.Where(p => p.SmallestCandidateBytes > 0)
                                       .Select(p => p.SmallestCandidateBytes);

        return new AppendVolumePlan
        {
            VolumeId              = volume.Id,
            VolumeLabel           = volume.Label,
            DatLineId             = "",                  // aggregate — never used for execution
            VolumeRootPath        = groupRoot,
            TargetCapacityBytes   = volume.PlannedSizeBytes,
            TargetUsedBytes       = volume.ActualSizeBytes,
            TargetFreeBytes       = targetFree,
            TotalDerivedArtifactsForDatLine = leafPlans.Sum(p => p.TotalDerivedArtifactsForDatLine),
            ReleaseUnwantedSkipped  = leafPlans.Sum(p => p.ReleaseUnwantedSkipped),
            TotalCandidates         = leafPlans.Sum(p => p.TotalCandidates),
            AlreadyAssignedSkipped  = leafPlans.Sum(p => p.AlreadyAssignedSkipped),
            ArchiveMissingSkipped   = leafPlans.Sum(p => p.ArchiveMissingSkipped),
            TargetCollisionSkipped  = leafPlans.Sum(p => p.TargetCollisionSkipped),
            TooLargeSkipped         = leafPlans.Sum(p => p.TooLargeSkipped),
            InvalidHashSkipped      = leafPlans.Sum(p => p.InvalidHashSkipped),
            ExcludedIncomingSkipPath  = leafPlans.Sum(p => p.ExcludedIncomingSkipPath),
            ExcludedZeroOrInvalidSize = leafPlans.Sum(p => p.ExcludedZeroOrInvalidSize),
            PlannedCount             = plannedCount,
            PlannedBytes             = plannedBytes,
            SkippedCount             = leafPlans.Sum(p => p.SkippedCount),
            RemainingTargetFreeBytes = targetFree - plannedBytes,
            TargetBytesAfter         = volume.ActualSizeBytes + plannedBytes,
            LargestCandidateBytes    = leafPlans.Count > 0 ? leafPlans.Max(p => p.LargestCandidateBytes) : 0,
            SmallestCandidateBytes   = nonZeroSmallest.Any() ? nonZeroSmallest.Min() : 0,
            TotalCandidateBytes      = leafPlans.Sum(p => p.TotalCandidateBytes),
            ActiveArchivePhysicalFileCount         = leafPlans.Sum(p => p.ActiveArchivePhysicalFileCount),
            ActiveArchiveKnownWantedFileCount      = leafPlans.Sum(p => p.ActiveArchiveKnownWantedFileCount),
            ActiveArchiveUnassignedWantedFileCount = leafPlans.Sum(p => p.ActiveArchiveUnassignedWantedFileCount),
            Entries               = entries,
            SkipReasonCounts      = skipCounts,
            CanExecute            = plannedCount > 0,
        };
    }

    private static GroupAppendVolumePlan Blocked(VolumeRecord volume, string collectionDisplay, string reason)
        => new()
        {
            VolumeId          = volume.Id,
            GroupId           = volume.GroupId ?? "",
            VolumeLabel       = volume.Label,
            CollectionDisplay = collectionDisplay,
            LeafPlans         = Array.Empty<AppendVolumePlan>(),
            Aggregate         = BuildAggregate(volume, volume.Label, Array.Empty<AppendVolumePlan>()),
            BlockReason       = reason,
        };
}
