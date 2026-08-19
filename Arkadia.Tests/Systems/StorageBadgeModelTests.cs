using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>Completion + lifecycle + STAGING-independence on the view models, and the Group Details Missing filter
/// (computed purely over the loaded rows — the same predicate the dialog toggles in memory).</summary>
public sealed class StorageBadgeModelTests
{
    // ── DatLineInfo (Single DAT) ─────────────────────────────────────────────

    [Fact] // Single completion uses the wanted rule (present ÷ (releases − unwanted))
    public void Single_Completion_UsesWantedRule()
    {
        var d = new DatLineInfo("N", Releases: 30, "2026-01-01", Present: 5, Unwanted: 2);
        Assert.Equal(17, d.CompletionPercent);   // 5 / 28
        Assert.Equal("17%", d.CompletionText);
    }

    [Fact] // all unwanted → N/A
    public void Single_Completion_AllUnwanted_Na()
    {
        var d = new DatLineInfo("N", Releases: 5, "2026-01-01", Present: 0, Unwanted: 5);
        Assert.Null(d.CompletionPercent);
        Assert.Equal("N/A", d.CompletionText);
    }

    [Theory] // Single lifecycle from its facts
    [InlineData(0, 0, false, ArchiveLifecycle.None)]
    [InlineData(10, 3, false, ArchiveLifecycle.NeedsVolume)]
    [InlineData(10, 0, true, ArchiveLifecycle.ReadyToMove)]
    [InlineData(10, 0, false, ArchiveLifecycle.Archived)]
    public void Single_Lifecycle(int owned, int unassigned, bool local, ArchiveLifecycle expected)
    {
        var d = new DatLineInfo("N", Releases: 10, "x",
            OwnedPresentDerived: owned, Unassigned: unassigned, AnyLocalVolume: local);
        Assert.Equal(expected, d.Lifecycle);
    }

    [Fact] // STAGING is independent of lifecycle: an ARCHIVED DAT can still have staging material
    public void Single_Staging_IndependentOfLifecycle()
    {
        var d = new DatLineInfo("N", Releases: 10, "x",
            Present: 10, OwnedPresentDerived: 10, Unassigned: 0, AnyLocalVolume: false, HasStaging: true);
        Assert.Equal(ArchiveLifecycle.Archived, d.Lifecycle);   // lifecycle unaffected by staging
        Assert.True(d.HasStaging);
    }

    // ── GroupDatCardInfo ─────────────────────────────────────────────────────

    [Fact] // pending → no lifecycle (never a false state from partial data)
    public void Group_Lifecycle_PendingIsNone()
    {
        var card = new GroupDatCardInfo("g", "G", "A", "sys", 1, 0, 0)
            { CoveragePending = true, OwnedPresentDerived = 10, Unassigned = 5 };
        Assert.Equal(ArchiveLifecycle.None, card.Lifecycle);
    }

    [Fact] // resolved → lifecycle from facts; STAGING independent
    public void Group_Lifecycle_ResolvedAndStagingIndependent()
    {
        var card = new GroupDatCardInfo("g", "G", "A", "sys", 3, PresentSum: 5, WantedSum: 28)
            { OwnedPresentDerived = 10, Unassigned = 0, AnyLocalVolume = false, HasStaging = true };
        Assert.Equal(ArchiveLifecycle.Archived, card.Lifecycle);
        Assert.True(card.HasStaging);
        Assert.Equal(17, card.CoveragePercent);   // still the weighted ratio
    }

    // ── Group Details Missing filter ─────────────────────────────────────────

    private static GroupLeafRecord Leaf(string id, int releases) =>
        new(new DatLineRecord { Id = id, ReleaseCount = releases, MediaTypeId = "other" },
            new DatLineGroupMetadataRecord { RelativeDatPath = id + "/g.dat" });

    [Fact] // BuildRows maps present/unwanted and computes completion; a 100% leaf is not missing, an incomplete one is
    public void Details_BuildRows_CompletionAndMissing()
    {
        var leaves = new[] { Leaf("full", 10), Leaf("part", 10), Leaf("na", 4) };
        var media  = new Dictionary<string, string> { ["other"] = "Other" };
        var present  = new Dictionary<string, int> { ["full"] = 10, ["part"] = 4, ["na"] = 0 };
        var unwanted = new Dictionary<string, int> { ["na"] = 4 };   // all unwanted → N/A

        var rows = GroupDatDetails.BuildRows(leaves, media, present, unwanted);

        Assert.Equal("100%", rows.Single(r => r.LeafId == "full").CompletionText);
        Assert.Equal("40%",  rows.Single(r => r.LeafId == "part").CompletionText);
        Assert.Equal("N/A",  rows.Single(r => r.LeafId == "na").CompletionText);

        // Missing = incomplete only (completion, not storage). 100% hidden; N/A excluded; only "part" remains.
        var missing = rows.Where(r => r.IsIncomplete).ToList();
        Assert.Single(missing);
        Assert.Equal("part", missing[0].LeafId);
        Assert.Equal(1, rows.Count(r => r.IsIncomplete));   // Missing (N) count

        // Toggling the filter off restores the full set (the dialog keeps _allRows unchanged).
        Assert.Equal(3, rows.Count);
    }
}
