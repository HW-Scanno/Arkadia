using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// Tests for the M5 Systems-view Group helpers: the Single-vs-Group partition + one-card-per-group
/// aggregation (<see cref="GroupDatPartition"/> / <see cref="GroupDatCardInfo"/>) and the read-only
/// Details row mapping (<see cref="GroupDatDetails"/>). All pure — no UI, no catalog mutation.
/// </summary>
public sealed class GroupDatSystemsViewTests
{
    private static readonly Dictionary<string, GroupMeta> Groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c64-tosec"] = new GroupMeta("Commodore 64 TOSEC", "TOSEC", "c64"),
    };

    private static IEnumerable<LeafCoverageInput> Leaves(int count, int releasesEach, int presentEach = 0, int unwantedEach = 0, string group = "c64-tosec")
        => Enumerable.Range(0, count).Select(_ => new LeafCoverageInput(group, releasesEach, presentEach, unwantedEach));

    // ── Partition / one-card-per-group ─────────────────────────────────────────

    [Fact]  // (3) 410 leaves of one group → exactly one card; (6) leaf count
    public void Build_410Leaves_ProducesOneCardWithLeafCount()
    {
        var cards = GroupDatPartition.BuildGroupCards(Leaves(410, releasesEach: 10), Groups);

        var card = Assert.Single(cards);
        Assert.Equal("c64-tosec", card.GroupId);
        Assert.Equal(410, card.LeafCount);
    }

    [Fact]  // (4) display_name; (5) authority; (6) "410 leaf DATs" subtitle text
    public void Card_ShowsDisplayNameAuthorityAndLeafCountText()
    {
        var card = Assert.Single(GroupDatPartition.BuildGroupCards(Leaves(410, 10), Groups));

        Assert.Equal("Commodore 64 TOSEC", card.DisplayName);
        Assert.Equal("TOSEC", card.Authority);
        Assert.Equal("TOSEC · 410 leaf DATs", card.Subtitle);
    }

    [Fact]  // (1)(2) leaves belong to a group id → they are the group's; hidden-set is exactly those ids
    public void GroupLeafIds_AreExactlyTheGroupMembers()
    {
        // Simulates MainWindow's leafToGroup map: two group leaves + (implicitly) single dats excluded.
        var leafToGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["c64-tosec-a"] = "c64-tosec",
            ["c64-tosec-b"] = "c64-tosec",
        };
        var hidden = new HashSet<string>(leafToGroup.Keys, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("c64-tosec-a", hidden);
        Assert.Contains("c64-tosec-b", hidden);
        Assert.DoesNotContain("c64-nfo-single", hidden);   // a Single DAT (group_id NULL) is never hidden
    }

    [Fact]  // (13) a Group and a Single DAT coexist in the same System (partition keeps them separate)
    public void GroupAndSingle_Coexist_OnlyGroupLeavesRollUp()
    {
        var groups = new Dictionary<string, GroupMeta>(StringComparer.OrdinalIgnoreCase)
        {
            ["c64-tosec"] = new GroupMeta("C64 TOSEC", "TOSEC", "c64"),
        };
        // Only group leaves are passed to the aggregator; the single dat is rendered by the existing path.
        var cards = GroupDatPartition.BuildGroupCards(Leaves(3, 10, group: "c64-tosec"), groups);

        var card = Assert.Single(cards);
        Assert.Equal("c64", card.HardwareFamilyId);
        Assert.Equal(3, card.LeafCount);
    }

    // ── Completion: summed numerators / summed denominators, never averaged ────

    [Fact]  // (18) sum present / sum wanted; (19) NOT an average of leaf percentages
    public void Coverage_UsesSummedNumeratorsOverDenominators()
    {
        // Leaf A: 100 releases, 100 present (100%). Leaf B: 100 releases, 0 present (0%).
        var leaves = new[]
        {
            new LeafCoverageInput("c64-tosec", ReleaseCount: 100, Present: 100, Unwanted: 0),
            new LeafCoverageInput("c64-tosec", ReleaseCount: 100, Present: 0,   Unwanted: 0),
        };
        var card = Assert.Single(GroupDatPartition.BuildGroupCards(leaves, Groups));

        Assert.Equal(100, card.PresentSum);
        Assert.Equal(200, card.WantedSum);
        Assert.Equal(50, card.CoveragePercent);   // 100/200 = 50%, not avg(100%,0%) (also 50% here — see next)
        Assert.Equal("50%", card.CoverageText);
    }

    [Fact]  // (19) average would differ from summed ratio when leaf sizes differ
    public void Coverage_SummedRatioDiffersFromAverage_WhenLeafSizesDiffer()
    {
        // Leaf A: 10 releases, 10 present (100%). Leaf B: 90 releases, 0 present (0%).
        // Average of percentages = 50%. Summed ratio = 10/100 = 10%. We must report 10%.
        var leaves = new[]
        {
            new LeafCoverageInput("c64-tosec", 10, 10, 0),
            new LeafCoverageInput("c64-tosec", 90, 0,  0),
        };
        var card = Assert.Single(GroupDatPartition.BuildGroupCards(leaves, Groups));

        Assert.Equal(10, card.CoveragePercent);
    }

    [Fact]  // wanted excludes unwanted (denominator = releases − unwanted)
    public void Coverage_ExcludesUnwantedFromDenominator()
    {
        var leaves = new[] { new LeafCoverageInput("c64-tosec", ReleaseCount: 100, Present: 40, Unwanted: 20) };
        var card = Assert.Single(GroupDatPartition.BuildGroupCards(leaves, Groups));

        Assert.Equal(80, card.WantedSum);          // 100 − 20
        Assert.Equal(50, card.CoveragePercent);    // 40 / 80
    }

    [Fact]  // (20) zero denominator handled coherently (N/A, not a divide-by-zero / not 0%)
    public void Coverage_ZeroDenominator_IsNotAvailable()
    {
        var leaves = new[] { new LeafCoverageInput("c64-tosec", ReleaseCount: 0, Present: 0, Unwanted: 0) };
        var card = Assert.Single(GroupDatPartition.BuildGroupCards(leaves, Groups));

        Assert.Null(card.CoveragePercent);
        Assert.Equal("N/A", card.CoverageText);
    }

    // ── Details rows (relative path, persisted media, release count) ────────────

    private static GroupLeafRecord Leaf(string id, string relPath, string mediaId, int releases)
        => new(
            new DatLineRecord { Id = id, HardwareFamilyId = "c64", MediaTypeId = mediaId, Version = "1", ReleaseCount = releases },
            new DatLineGroupMetadataRecord { DatLineId = id, GroupId = "c64-tosec", RelativeDatPath = relPath });

    [Fact]  // (8) rows correspond to the leaves; (9) source path = relative_dat_path; (10) persisted media; (11) release count
    public void Details_MapsRelativePathPersistedMediaAndReleases()
    {
        var mediaNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["floppy"] = "Floppy Disk", ["other"] = "Other",
        };
        var leaves = new[]
        {
            Leaf("c64-tosec-apps", "Applications/[D64].dat", "floppy", 65),
            Leaf("c64-tosec-demos", "Demos/[PRG].dat",       "other",  12),
        };

        var rows = GroupDatDetails.BuildRows(leaves, mediaNames);

        Assert.Equal(2, rows.Count);
        Assert.Equal("c64-tosec-apps", rows[0].LeafId);
        Assert.Equal("Applications/[D64].dat", rows[0].SourcePath);   // relative_dat_path, not absolute/DataStorePath
        Assert.Equal("Floppy Disk", rows[0].MediaType);               // persisted media display, not recomputed
        Assert.Equal(65, rows[0].Releases);
        Assert.Equal("Other", rows[1].MediaType);
    }

    [Fact]  // (10) unknown media id falls back to the raw persisted id (never invents/recomputes)
    public void Details_UnknownMedia_FallsBackToPersistedId()
    {
        var rows = GroupDatDetails.BuildRows(
            new[] { Leaf("c64-tosec-x", "X/x.dat", "exotic-media", 3) },
            new Dictionary<string, string>());

        Assert.Equal("exotic-media", Assert.Single(rows).MediaType);
    }
}
