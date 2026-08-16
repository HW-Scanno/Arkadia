using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Arkadia.Data;

/// <summary>Overall readiness of a Group volume plan.</summary>
public enum GroupVolumeReadiness
{
    /// <summary>Every leaf DB was read and there is at least one release to include.</summary>
    Ready,
    /// <summary>At least one leaf DB is missing/unreadable — the plan is not presented as complete.</summary>
    Blocked,
    /// <summary>All leaves read cleanly but nothing is includable (empty, all already assigned, etc.).</summary>
    NothingToDo,
}

/// <summary>Per-leaf input to the Group packer: the leaf identity plus its Single planning candidates (in Single order).</summary>
public sealed record GroupLeafPlanInput(
    string                          DatLineId,
    string                          DisplayName,
    IReadOnlyList<PlanningCandidate> Candidates);

/// <summary>One decided Group planning row: the authoritative leaf id + the reused Single per-release decision.</summary>
public sealed record GroupPlanningItem(string DatLineId, PlanningDecision Decision)
{
    // Identity is (DatLineId, ReleaseId) — ReleaseId is NOT unique across leaves.
    public string ReleaseId => Decision.ReleaseId;
}

/// <summary>Per-leaf slice of a Group plan (regrouped under the authoritative DatLineId).</summary>
public sealed record GroupLeafPlanningResult(
    string                        DatLineId,
    string                        DisplayName,
    IReadOnlyList<GroupPlanningItem> Items,
    long                          PlannedBytes,
    int                           IncludeCount,
    int                           SkipCount,
    int                           DeferCount);

/// <summary>Frozen, read-only result of a Group volume plan. V-G2 never persists it.</summary>
public sealed class GroupVolumePlanningResult
{
    public required string                                   VolumeId             { get; init; }
    public required string                                   GroupId              { get; init; }
    public required long                                     CapacityBytes        { get; init; }
    public required long                                     ActualSizeBytes      { get; init; }
    public required long                                     RemainingBeforeBytes { get; init; }
    public required long                                     PlannedBytes         { get; init; }
    public required long                                     RemainingAfterBytes  { get; init; }
    public required ImmutableArray<GroupLeafPlanningResult>  Leaves               { get; init; }
    public required ImmutableArray<string>                   Diagnostics          { get; init; }
    public required GroupVolumeReadiness                     Readiness            { get; init; }

    public int IncludeCount => Leaves.Sum(l => l.IncludeCount);
    public int SkipCount    => Leaves.Sum(l => l.SkipCount);
    public int DeferCount   => Leaves.Sum(l => l.DeferCount);
}

/// <summary>
/// Pure Group volume packer. Reuses the EXACT Single capacity algorithm (<see cref="VolumePlanner.Plan"/>) over
/// ONE global budget: it flattens every leaf's Single candidates (leaves ordered by DatLineId Ordinal, Single
/// candidate order preserved within each leaf) into one stream, runs the single planner once, zips the 1:1
/// positional decisions back to their tagged leaf, and regroups under the authoritative DatLineId. No per-leaf
/// capacity pass, no re-sorting, no eligibility reinterpretation. Stateless and side-effect free.
/// </summary>
public static class GroupVolumePlanner
{
    public static GroupVolumePlanningResult Plan(
        string                          volumeId,
        string                          groupId,
        long                            capacityBytes,
        long                            actualSizeBytes,
        IReadOnlyList<GroupLeafPlanInput> leaves,
        IEnumerable<string>?            diagnostics = null,
        GroupVolumeReadiness?           forcedReadiness = null)
    {
        // Deterministic global order: leaves by DatLineId Ordinal; Single candidate order preserved within a leaf.
        var ordered = leaves.OrderBy(l => l.DatLineId, StringComparer.Ordinal).ToList();

        // Flatten, tagging each candidate with its leaf, preserving order.
        var tags  = new List<string>();
        var flat  = new List<PlanningCandidate>();
        foreach (var leaf in ordered)
            foreach (var c in leaf.Candidates)
            {
                tags.Add(leaf.DatLineId);
                flat.Add(c);
            }

        // ONE global capacity pass — the identical Single algorithm.
        var packed = VolumePlanner.Plan(capacityBytes, actualSizeBytes, flat);

        // Zip decisions back to their leaf by stable position (VolumePlanner preserves input order 1:1).
        var items = new GroupPlanningItem[packed.Items.Count];
        for (int i = 0; i < items.Length; i++)
            items[i] = new GroupPlanningItem(tags[i], packed.Items[i]);

        // Regroup under DatLineId (flat order is already leaf-contiguous → keep the ordered-leaf sequence).
        var leafResults = ImmutableArray.CreateBuilder<GroupLeafPlanningResult>(ordered.Count);
        int cursor = 0;
        foreach (var leaf in ordered)
        {
            var slice = new List<GroupPlanningItem>(leaf.Candidates.Count);
            for (int k = 0; k < leaf.Candidates.Count; k++)
                slice.Add(items[cursor++]);

            long plannedBytes = slice.Where(x => x.Decision.Decision == "include").Sum(x => x.Decision.TotalSizeBytes);
            leafResults.Add(new GroupLeafPlanningResult(
                leaf.DatLineId, leaf.DisplayName, slice, plannedBytes,
                IncludeCount: slice.Count(x => x.Decision.Decision == "include"),
                SkipCount:    slice.Count(x => x.Decision.Decision == "skip"),
                DeferCount:   slice.Count(x => x.Decision.Decision == "defer")));
        }

        var diags = (diagnostics ?? Enumerable.Empty<string>()).ToImmutableArray();
        var readiness = forcedReadiness ?? (
            diags.Length > 0                     ? GroupVolumeReadiness.Blocked :
            packed.PlannedBytes > 0              ? GroupVolumeReadiness.Ready :
                                                   GroupVolumeReadiness.NothingToDo);

        return new GroupVolumePlanningResult
        {
            VolumeId             = volumeId,
            GroupId              = groupId,
            CapacityBytes        = capacityBytes,
            ActualSizeBytes      = actualSizeBytes,
            RemainingBeforeBytes = packed.RemainingBytesBeforePlanning,
            PlannedBytes         = packed.PlannedBytes,
            RemainingAfterBytes  = packed.RemainingBytesAfterPlanning,
            Leaves               = leafResults.ToImmutable(),
            Diagnostics          = diags,
            Readiness            = readiness,
        };
    }
}
