using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// Integration checks for the Group Details path over a real <see cref="CatalogService"/> (temp catalog.db —
/// never the runtime data): the single <see cref="CatalogService.GetLeavesForGroup"/> query returns the
/// group's leaves, the rows map 1:1 with no duplication, and building the view performs no catalog mutation.
/// </summary>
public sealed class GroupDatSystemsIntegrationTests : IDisposable
{
    private readonly string _dir;

    public GroupDatSystemsIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkGrpSysView_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private CatalogService SeededCatalog(int leafCount)
    {
        var c = new CatalogService(_dir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = "c64", Name = "Commodore 64", Manufacturer = "Commodore", HardwareTypeId = "" },
        });
        var leaves = Enumerable.Range(0, leafCount).Select(i => new GroupDatCatalogLeafCreate
        {
            DatLine = new DatLineRecord
            {
                Id = $"c64-tosec-{i:D3}", HardwareFamilyId = "c64", Name = "other", Authority = "tosec",
                MediaTypeId = "other", Version = "1", DataStorePath = $"systems/c64/c64-tosec-{i:D3}.db",
                ReleaseCount = i, ImportedAtUtc = DateTime.UtcNow,
            },
            RelativeDatPath = $"Set{i:D3}/game.dat", SourceDatName = "game.dat",
            SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
        }).ToArray();
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = "c64-tosec", DisplayName = "Commodore 64 TOSEC", HardwareFamilyId = "c64",
            Authority = "tosec", Leaves = leaves,
        });
        return c;
    }

    [Fact]  // (7)(8) Details resolves the correct group's leaves via a single query; (12) no duplication
    public void Details_ReturnsAllLeaves_NoDuplication()
    {
        var c = SeededCatalog(410);

        var leaves = c.GetLeavesForGroup("c64-tosec");
        Assert.Equal(410, leaves.Count);

        var rows = GroupDatDetails.BuildRows(leaves,
            c.GetMediaTypes().ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase));

        Assert.Equal(410, rows.Count);
        Assert.Equal(410, rows.Select(r => r.LeafId).Distinct().Count());   // no duplicated leaf
        Assert.All(rows, r => Assert.StartsWith("Set", r.SourcePath));       // relative_dat_path used
        Assert.Contains(rows, r => r.LeafId == "c64-tosec-000" && r.Releases == 0);
        Assert.Contains(rows, r => r.LeafId == "c64-tosec-409" && r.Releases == 409);
    }

    [Fact]  // (15) building the Systems Group view mutates neither the catalog nor the group data
    public void BuildingGroupView_DoesNotMutateCatalog()
    {
        var c = SeededCatalog(20);

        int groupsBefore = c.LoadDatGroups().Count;
        int leavesBefore = c.GetLeavesForGroup("c64-tosec").Count;
        int linesBefore  = c.LoadDatLines().Count;

        // Simulate exactly what the view does: read leaves, read media, build rows.
        var leaves = c.GetLeavesForGroup("c64-tosec");
        var rows   = GroupDatDetails.BuildRows(leaves,
            c.GetMediaTypes().ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase));
        _ = GroupDatPartition.BuildGroupCards(
            leaves.Select(l => new LeafCoverageInput("c64-tosec", l.DatLine.ReleaseCount, 0, 0)),
            new Dictionary<string, GroupMeta> { ["c64-tosec"] = new GroupMeta("Commodore 64 TOSEC", "TOSEC", "c64") });

        Assert.Equal(20, rows.Count);
        Assert.Equal(groupsBefore, c.LoadDatGroups().Count);
        Assert.Equal(leavesBefore, c.GetLeavesForGroup("c64-tosec").Count);
        Assert.Equal(linesBefore,  c.LoadDatLines().Count);
        Assert.True(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
    }

    [Fact]  // (14) group leaves are real dat_lines counted once; the Group card is not itself a dat_line
    public void GroupLeaves_AreRealDatLines_CountedOnce()
    {
        var c = SeededCatalog(410);

        var lines = c.LoadDatLines();
        Assert.Equal(410, lines.Count);   // 410 real leaves — the Group card adds no extra dat_line

        // The hidden-set used by the Systems view equals exactly the group's leaves.
        var leafToGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in c.LoadDatGroups())
            foreach (var leaf in c.GetLeavesForGroup(g.Id.Value))
                leafToGroup[leaf.DatLine.Id] = g.Id.Value;

        var visibleSingles = lines.Where(l => !leafToGroup.ContainsKey(l.Id)).ToList();
        Assert.Empty(visibleSingles);     // all 410 are group leaves → zero Single-DAT cards
    }
}
