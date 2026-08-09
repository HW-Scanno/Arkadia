using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// M6 startup-performance guarantees. Uses <see cref="DatLineStore.ConstructionCount"/> (an internal
/// diagnostic counter) to prove that rendering a Group card at startup opens ZERO leaf databases, while the
/// lazy coverage load opens exactly the group's leaves (off the UI thread). Real CatalogService + real leaf
/// DBs over a temp directory — never the runtime data.
/// </summary>
public sealed class GroupDatStartupCostTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dataDir;

    public GroupDatStartupCostTests()
    {
        _dir     = Path.Combine(Path.GetTempPath(), "ArkStartup_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static List<DatParser.ParsedGame> Games(int n) =>
        Enumerable.Range(0, n).Select(i => new DatParser.ParsedGame
        {
            Name = $"Game {i}", Roms = new List<DatParser.ParsedRom> { new() { Name = $"g{i}.bin", Size = "1", Sha1 = new string('a', 40) } },
        }).ToList();

    /// <summary>Builds a catalog with a c64 system and a group of <paramref name="leafCount"/> real leaf DBs.</summary>
    private CatalogService SeedGroupWithLeafDbs(int leafCount, int releasesPerLeaf = 3)
    {
        var c = new CatalogService(_dataDir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = "c64", Name = "Commodore 64", Manufacturer = "Commodore", HardwareTypeId = "" },
        });

        var leaves = new List<GroupDatCatalogLeafCreate>(leafCount);
        for (int i = 0; i < leafCount; i++)
        {
            var id  = $"c64-tosec-{i:D3}";
            var rel = $"systems/c64/{id}.db";
            LeafDatDatabaseBuilder.Build(Path.Combine(_dataDir, rel), id, Games(releasesPerLeaf), null, CancellationToken.None);
            leaves.Add(new GroupDatCatalogLeafCreate
            {
                DatLine = new DatLineRecord
                {
                    Id = id, HardwareFamilyId = "c64", Name = "other", Authority = "tosec", MediaTypeId = "other",
                    Version = "1", DataStorePath = rel, ReleaseCount = releasesPerLeaf, ImportedAtUtc = DateTime.UtcNow,
                },
                RelativeDatPath = $"Set{i:D3}/g.dat", SourceDatName = "g.dat",
                SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
            });
        }
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = "c64-tosec", DisplayName = "Commodore 64 TOSEC", HardwareFamilyId = "c64",
            Authority = "tosec", Leaves = leaves,
        });
        return c;
    }

    // Mirrors MainWindow.RefreshSystems' catalog-only group-card path (the startup path).
    private static List<GroupDatCardInfo> BuildPendingCards(CatalogService c)
    {
        var cards = new List<GroupDatCardInfo>();
        foreach (var g in c.LoadDatGroups())
        {
            var leaves = c.GetLeavesForGroup(g.Id.Value);
            cards.Add(new GroupDatCardInfo(g.Id.Value, g.DisplayName, g.Authority, g.HardwareFamilyId, leaves.Count, 0, 0)
            { CoveragePending = true });
        }
        return cards;
    }

    [Fact]  // (1)(2) 410 Group leaves → a card, with ZERO leaf DB opens on the startup path
    public void Startup_GroupCard_OpensNoLeafDatabases()
    {
        var c = SeedGroupWithLeafDbs(410);

        DatLineStore.ConstructionCount = 0;
        var cards = BuildPendingCards(c);

        Assert.Equal(0, DatLineStore.ConstructionCount);   // no leaf DB opened just to render the card
        var card = Assert.Single(cards);
        Assert.Equal(410, card.LeafCount);                 // (3) count from catalog
        Assert.True(card.CoveragePending);
        Assert.Null(card.CoveragePercent);                 // (4) never a false 0% while pending
        Assert.Equal("…", card.CoverageText);
    }

    [Fact]  // lazy coverage opens EXACTLY the group's leaves (off the UI thread), and only when requested
    public void LazyCoverage_OpensExactlyLeafCount()
    {
        var c      = SeedGroupWithLeafDbs(50);
        var leaves = c.GetLeavesForGroup("c64-tosec")
            .Select(l => (l.DatLine.DataStorePath, l.DatLine.ReleaseCount)).ToList();

        DatLineStore.ConstructionCount = 0;
        var (present, unwanted) = GroupCoverageLoader.Compute(leaves, _dataDir);

        Assert.Equal(50, DatLineStore.ConstructionCount);  // one open per leaf, no more
        Assert.Equal(0, present);                          // freshly built → all 'missing'
        Assert.Equal(0, unwanted);
    }

    [Fact]  // (4) lazy completion reproduces the M5 formula: Σ present / Σ (releases − unwanted)
    public void LazyCoverage_MatchesM5Formula()
    {
        var c = SeedGroupWithLeafDbs(3, releasesPerLeaf: 10);   // 3 leaves × 10 releases = 30 total

        // Leaf 000: mark 5 present. Leaf 001: mark 2 unwanted. Leaf 002: untouched.
        MarkStatuses("c64-tosec-000", present: 5);
        MarkStatuses("c64-tosec-001", unwanted: 2);

        var leaves = c.GetLeavesForGroup("c64-tosec")
            .Select(l => (l.DatLine.DataStorePath, l.DatLine.ReleaseCount)).ToList();
        var (present, unwanted) = GroupCoverageLoader.Compute(leaves, _dataDir);

        int releaseSum = leaves.Sum(l => l.ReleaseCount);   // 30
        int wanted     = releaseSum - unwanted;             // 30 − 2 = 28
        var card = new GroupDatCardInfo("c64-tosec", "G", "TOSEC", "c64", 3, present, wanted);

        Assert.Equal(5, present);
        Assert.Equal(2, unwanted);
        Assert.Equal(17, card.CoveragePercent);             // 5 / 28 = 17% (integer), NOT an average of leaf %s
    }

    [Fact]  // zero wanted → N/A (distinct from pending); pending → null (spinner)
    public void Coverage_PendingVsZeroDenominator_AreDistinct()
    {
        var pending = new GroupDatCardInfo("g", "G", "A", "sys", 1, 0, 0) { CoveragePending = true };
        var zero    = new GroupDatCardInfo("g", "G", "A", "sys", 1, 0, 0);   // resolved, no wanted

        Assert.Null(pending.CoveragePercent);
        Assert.Equal("…", pending.CoverageText);

        Assert.Null(zero.CoveragePercent);
        Assert.Equal("N/A", zero.CoverageText);
    }

    private void MarkStatuses(string leafId, int present = 0, int unwanted = 0)
    {
        var store = new DatLineStore(Path.Combine(_dataDir, "systems", "c64", leafId + ".db"));
        var ids   = store.LoadReleases().Select(r => r.Id).ToList();
        int idx = 0;
        for (int i = 0; i < present && idx < ids.Count; i++, idx++) store.UpdateReleaseStatus(ids[idx], "present");
        for (int i = 0; i < unwanted && idx < ids.Count; i++, idx++) store.UpdateReleaseStatus(ids[idx], "unwanted");
    }
}
