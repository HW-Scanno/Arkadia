using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// Pure Group volume packer: reuses the exact Single <see cref="VolumePlanner"/> algorithm over ONE global
/// capacity budget, deterministic ordering (leaves by DatLineId Ordinal, Single order within a leaf), 1:1
/// positional zip, and DatLineId-scoped regrouping/identity.
/// </summary>
public sealed class GroupVolumePlannerTests
{
    private static PlanningCandidate Cand(
        string releaseId, string releaseName, long size, bool assigned = false, bool complete = true, int derived = 1)
        => new()
        {
            ReleaseId = releaseId, ReleaseName = releaseName, TotalSizeBytes = size, DerivedCount = derived,
            IsAlreadyAssignedToAnyVolume = assigned, IsCompleteInArchive = complete,
        };

    private static GroupLeafPlanInput Leaf(string id, params PlanningCandidate[] c) => new(id, id, c);

    private static GroupVolumePlanningResult Plan(long capacity, long actual, params GroupLeafPlanInput[] leaves)
        => GroupVolumePlanner.Plan("vol", "grp", capacity, actual, leaves);

    private static GroupPlanningItem Item(GroupVolumePlanningResult r, string datLineId, string releaseId)
        => r.Leaves.Single(l => l.DatLineId == datLineId).Items.Single(i => i.ReleaseId == releaseId);

    [Fact] // ONE global budget: capacity 100, leaf-a 70, leaf-b 70 → first include, second defer (NOT both)
    public void GlobalCapacity_SharedBudget()
    {
        var r = Plan(100, 0,
            Leaf("leaf-a", Cand("r1", "A", 70)),
            Leaf("leaf-b", Cand("r1", "B", 70)));

        Assert.Equal("include", Item(r, "leaf-a", "r1").Decision.Decision);
        Assert.Equal("defer",   Item(r, "leaf-b", "r1").Decision.Decision);
        Assert.Equal(70, r.PlannedBytes);
        Assert.Equal(1, r.IncludeCount);
        Assert.Equal(1, r.DeferCount);
    }

    [Fact] // a later smaller candidate still fits after a defer (Single behavior preserved)
    public void LaterSmallerCandidate_Fits()
    {
        var r = Plan(100, 0,
            Leaf("leaf-a", Cand("r", "A", 70)),
            Leaf("leaf-b", Cand("r", "B", 50)),
            Leaf("leaf-c", Cand("r", "C", 20)));

        Assert.Equal("include", Item(r, "leaf-a", "r").Decision.Decision);   // 70 → rem 30
        Assert.Equal("defer",   Item(r, "leaf-b", "r").Decision.Decision);   // 50 > 30
        Assert.Equal("include", Item(r, "leaf-c", "r").Decision.Decision);   // 20 ≤ 30
        Assert.Equal(90, r.PlannedBytes);
    }

    [Fact] // remaining = max(0, planned - actual); actual consumes the shared budget
    public void Capacity_UsesActualSize()
    {
        var r = Plan(100, 80, Leaf("leaf-a", Cand("r", "A", 30)));   // remaining 20 < 30
        Assert.Equal("defer", Item(r, "leaf-a", "r").Decision.Decision);
        Assert.Equal(20, r.RemainingBeforeBytes);
    }

    [Fact] // identity is (DatLineId, ReleaseId): identical ReleaseId/Name across leaves stay distinct
    public void PerLeafIdentity_SameReleaseId_DistinctLeaves()
    {
        var r = Plan(1000, 0,
            Leaf("leaf-a", Cand("r1", "Game", 10)),
            Leaf("leaf-b", Cand("r1", "Game", 10)));

        Assert.Equal(2, r.Leaves.Length);
        Assert.Equal("include", Item(r, "leaf-a", "r1").Decision.Decision);
        Assert.Equal("include", Item(r, "leaf-b", "r1").Decision.Decision);
        Assert.Equal("leaf-a", r.Leaves[0].DatLineId);
        Assert.Equal("leaf-b", r.Leaves[1].DatLineId);
    }

    [Fact] // the Group wrapper decisions EQUAL Single VolumePlanner.Plan over the same flattened stream
    public void SingleSemantics_Preserved()
    {
        var leaves = new[]
        {
            Leaf("leaf-a", Cand("r1", "A1", 40), Cand("r2", "A2", 40, assigned: true)),
            Leaf("leaf-b", Cand("r1", "B1", 40, complete: false), Cand("r2", "B2", 40)),
        };
        var r = GroupVolumePlanner.Plan("v", "g", 100, 0, leaves);

        // Reconstruct the exact flat stream the Group planner packs (leaves already Ordinal-ordered here).
        var flat = leaves.SelectMany(l => l.Candidates).ToList();
        var single = VolumePlanner.Plan(100, 0, flat);

        var groupFlat = r.Leaves.SelectMany(l => l.Items).ToList();
        Assert.Equal(single.Items.Count, groupFlat.Count);
        for (int i = 0; i < groupFlat.Count; i++)
        {
            Assert.Equal(single.Items[i].ReleaseId, groupFlat[i].Decision.ReleaseId);   // 1:1 positional zip
            Assert.Equal(single.Items[i].Decision,  groupFlat[i].Decision.Decision);
            Assert.Equal(single.Items[i].Reason,    groupFlat[i].Decision.Reason);
        }
        Assert.Equal(single.PlannedBytes, r.PlannedBytes);
    }

    [Fact] // already-assigned → skip "already assigned" (exactly Single)
    public void AssignmentExclusion_Skips()
    {
        var r = Plan(1000, 0, Leaf("leaf-a", Cand("r", "A", 10, assigned: true)));
        var it = Item(r, "leaf-a", "r");
        Assert.Equal("skip", it.Decision.Decision);
        Assert.Equal("already assigned", it.Decision.Reason);
        Assert.Equal(0, r.PlannedBytes);
    }

    [Fact] // archive-incomplete → skip "archive incomplete" (exactly Single)
    public void ArchiveIncomplete_Skips()
    {
        var r = Plan(1000, 0, Leaf("leaf-a", Cand("r", "A", 10, complete: false)));
        var it = Item(r, "leaf-a", "r");
        Assert.Equal("skip", it.Decision.Decision);
        Assert.Equal("archive incomplete", it.Decision.Reason);
    }

    [Fact] // leaves supplied out of order are packed/regrouped in DatLineId Ordinal order — deterministic
    public void Determinism_OrdinalLeafOrder_StableResults()
    {
        GroupVolumePlanningResult Run() => Plan(1000, 0,
            Leaf("leaf-c", Cand("r", "C", 10)),
            Leaf("leaf-a", Cand("r", "A", 10)),
            Leaf("leaf-b", Cand("r", "B", 10)));

        var r1 = Run();
        var r2 = Run();
        Assert.Equal(new[] { "leaf-a", "leaf-b", "leaf-c" }, r1.Leaves.Select(l => l.DatLineId));
        Assert.Equal(r1.Leaves.Select(l => l.DatLineId), r2.Leaves.Select(l => l.DatLineId));
        Assert.Equal(r1.PlannedBytes, r2.PlannedBytes);
    }

    [Fact] // include/skip/defer regrouped under the correct DatLineId
    public void Regroup_PerLeafCounts()
    {
        var r = Plan(45, 0,
            Leaf("leaf-a", Cand("r1", "A1", 20), Cand("r2", "A2", 20, assigned: true)),   // include + skip
            Leaf("leaf-b", Cand("r1", "B1", 20), Cand("r2", "B2", 20)));                   // include + defer (rem 5)

        var a = r.Leaves.Single(l => l.DatLineId == "leaf-a");
        var b = r.Leaves.Single(l => l.DatLineId == "leaf-b");
        Assert.Equal((1, 1, 0), (a.IncludeCount, a.SkipCount, a.DeferCount));
        Assert.Equal((1, 0, 1), (b.IncludeCount, b.SkipCount, b.DeferCount));
        Assert.Equal(40, r.PlannedBytes);   // 20 + 20
    }

    [Fact] // readiness: Ready when something plans, NothingToDo when nothing includable, Blocked with diagnostics
    public void Readiness_States()
    {
        Assert.Equal(GroupVolumeReadiness.Ready,
            Plan(100, 0, Leaf("leaf-a", Cand("r", "A", 10))).Readiness);
        Assert.Equal(GroupVolumeReadiness.NothingToDo,
            Plan(100, 0, Leaf("leaf-a", Cand("r", "A", 10, assigned: true))).Readiness);
        Assert.Equal(GroupVolumeReadiness.Blocked,
            GroupVolumePlanner.Plan("v", "g", 100, 0,
                new[] { Leaf("leaf-a", Cand("r", "A", 10)) }, new[] { "leaf-x: missing" }).Readiness);
    }
}
