using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// Group Fillback planner (V-G7). PORTS the historical Single Fillback by merging source assignments across all
/// leaves into ONE globally-ordered candidate set and handing it to the shared
/// <see cref="VolumeFillbackPlanner.PlanCore"/> — the exact Single ordering / one-global-target-budget / collision /
/// skip / TooLarge-continue logic and the existing <see cref="VolumeFillbackService"/> executor are reused
/// unchanged. Scope: <b>same-GroupId Group → Group only</b> (mirrors Single's same-DatLine restriction). Each
/// candidate keeps its exact leaf <c>dat_line_id</c> (never the GroupId) and resolves leaf-nested source/target
/// paths (<c>&lt;groupRoot&gt;/&lt;leaf-segment&gt;/&lt;file&gt;</c>). Membership / segment-map / leaf-DB drift
/// BLOCKS before any mutation (surfaced as a non-executable plan with an issue). Reads only (leaf stores via
/// <see cref="DatLineStore.OpenReadOnly"/>).
/// </summary>
public static class GroupFillbackPlanningService
{
    public static VolumeFillbackPlan Plan(
        CatalogService                 catalog,
        string                         dataDir,
        VolumeRecord                   source,
        VolumeRecord                   target,
        string                         sourceGroupRoot,
        string                         targetGroupRoot,
        string                         sourceDiskLabel,
        string                         targetDiskLabel,
        IReadOnlyList<GroupLeafRecord> groupLeaves)
    {
        // ── Hard preconditions (mirror Single, in collection terms) ────────────
        string? issue =
            source.GroupId is not { } gid || target.GroupId is null || target.GroupId != gid
                ? "Source and target volumes must belong to the same collection."
            : source.Id == target.Id
                ? "Source and target volumes must be different."
            : !Directory.Exists(sourceGroupRoot)
                ? "Source volume disk is not mounted."
            : !Directory.Exists(targetGroupRoot)
                ? "Target volume disk is not mounted."
            : null;

        if (issue is not null)
            return Blocked(source, target, sourceGroupRoot, targetGroupRoot, sourceDiskLabel, targetDiskLabel, issue);

        var groupId = source.GroupId!;

        var sourceVAs = catalog.GetVolumeArtifacts(source.Id)
            .Where(va => va.Status == "present_in_final")
            .ToList();
        if (sourceVAs.Count == 0)
            return Blocked(source, target, sourceGroupRoot, targetGroupRoot, sourceDiskLabel, targetDiskLabel,
                "Source volume has no active artifacts.");

        // Authoritative leaf ids come from the assignment rows; validate the segment map for exactly those.
        var assignedLeafIds = sourceVAs.Select(v => v.DatLineId).Distinct(StringComparer.Ordinal)
                                       .OrderBy(id => id, StringComparer.Ordinal).ToList();
        var (segmentMap, segErr) = GroupRelativeLeafPath.BuildMap(groupId, assignedLeafIds);
        if (segErr is not null)
            return Blocked(source, target, sourceGroupRoot, targetGroupRoot, sourceDiskLabel, targetDiskLabel,
                $"Cannot fill back this Group volume — {segErr}");

        var membershipById = groupLeaves.ToDictionary(l => l.DatLine.Id, StringComparer.Ordinal);

        // ── Merge leaf-nested candidates across all leaves ─────────────────────
        var candidates = new List<VolumeFillbackPlanner.FillbackCandidate>();
        foreach (var leafId in assignedLeafIds)
        {
            if (!membershipById.TryGetValue(leafId, out var leaf))
                return Blocked(source, target, sourceGroupRoot, targetGroupRoot, sourceDiskLabel, targetDiskLabel,
                    $"Assigned leaf '{leafId}' is no longer a member of collection '{groupId}'. " +
                    "Reconcile the collection before filling back this volume.");

            var dsp = leaf.DatLine.DataStorePath;
            var abs = dsp.Length > 0 ? Path.Combine(dataDir, dsp) : "";
            if (abs.Length == 0 || !File.Exists(abs))
                return Blocked(source, target, sourceGroupRoot, targetGroupRoot, sourceDiskLabel, targetDiskLabel,
                    $"The database for leaf '{leafId}' is unavailable, so it cannot be planned.");

            var segment    = segmentMap[leafId];
            var leafVAs    = sourceVAs.Where(v => v.DatLineId == leafId).ToList();
            var leafDaIds  = leafVAs.Select(v => v.DerivedArtifactId).ToList();
            var vaByDaId   = leafVAs.ToDictionary(v => v.DerivedArtifactId, StringComparer.Ordinal);

            var store = DatLineStore.OpenReadOnly(abs);   // read-only planning
            foreach (var vi in store.GetFillbackCandidateInfos(leafDaIds))   // excludes unwanted, same as Single
            {
                if (!vaByDaId.TryGetValue(vi.DerivedArtifactId, out var va)) continue;
                var src = VolumeArtifactPathBuilder.GetGroupFlatFullPath(sourceGroupRoot, segment, vi.FileName);
                var dst = VolumeArtifactPathBuilder.GetGroupFlatFullPath(targetGroupRoot, segment, vi.FileName);
                candidates.Add(new VolumeFillbackPlanner.FillbackCandidate(va, vi, src, dst));
            }
        }

        // One mode for the whole plan (both group roots share a filesystem or don't).
        var mode = VolumeFillbackPlanner.IsSameDisk(sourceGroupRoot, targetGroupRoot)
            ? FillbackOperationMode.MoveSameDisk
            : FillbackOperationMode.CopyVerifyDeleteCrossDisk;

        // Reuse the exact Single planning core (ordering / one global budget / collision / skip taxonomy).
        return new VolumeFillbackPlanner(catalog).PlanCore(
            source, target, sourceGroupRoot, targetGroupRoot, sourceDiskLabel, targetDiskLabel, mode, candidates);
    }

    private static VolumeFillbackPlan Blocked(
        VolumeRecord source, VolumeRecord target,
        string sourceRoot, string targetRoot, string sourceDiskLabel, string targetDiskLabel, string reason)
        => new()
        {
            SourceVolumeId           = source.Id,
            SourceVolumeLabel        = source.Label,
            SourceDiskLabel          = sourceDiskLabel,
            SourceRootPath           = sourceRoot,
            TargetVolumeId           = target.Id,
            TargetVolumeLabel        = target.Label,
            TargetDiskLabel          = targetDiskLabel,
            TargetRootPath           = targetRoot,
            OperationMode            = FillbackOperationMode.CopyVerifyDeleteCrossDisk,
            TargetCapacityBytes      = target.PlannedSizeBytes,
            TargetUsedBytes          = target.ActualSizeBytes,
            TargetFreeBytes          = target.PlannedSizeBytes - target.ActualSizeBytes,
            PlannedBytes             = 0,
            PlannedCount             = 0,
            SkippedCount             = 0,
            RemainingTargetFreeBytes = target.PlannedSizeBytes - target.ActualSizeBytes,
            SourceBytesBefore        = source.ActualSizeBytes,
            SourceBytesAfter         = source.ActualSizeBytes,
            TargetBytesAfter         = target.ActualSizeBytes,
            Entries                  = [],
            Warnings                 = [],
            Issues                   = [reason],
            SkipReasonCounts         = new Dictionary<string, int>(StringComparer.Ordinal),
            CanExecute               = false,
        };
}
