using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// Undo-only Delete Group: the empty-Group happy path (catalog rows + leaf DB files removed), leaf-first / FK
/// ordering with foreign keys ON (blocked group-row delete rolls back), and every safety-gate refusal (group
/// volumes, leaf volume_artifacts, present/derived content, staging material) leaving all state intact.
/// Real CatalogService + real leaf DBs over a temp dir — never runtime data.
/// </summary>
public sealed class GroupDeleteServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dataDir;
    private readonly string _appRoot;

    public GroupDeleteServiceTests()
    {
        _dir     = Path.Combine(Path.GetTempPath(), "ArkGrpDel_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_dir, "data");
        _appRoot = Path.Combine(_dir, "app");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_appRoot);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static List<DatParser.ParsedGame> Games(int n) =>
        Enumerable.Range(0, n).Select(i => new DatParser.ParsedGame
        {
            Name = $"Game {i}", Roms = new List<DatParser.ParsedRom> { new() { Name = $"g{i}.bin", Size = "1", Sha1 = new string('a', 40) } },
        }).ToList();

    private CatalogService NewCatalog()
    {
        var c = new CatalogService(_dataDir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = "c64", Name = "Commodore 64", Manufacturer = "Commodore", HardwareTypeId = "" },
        });
        return c;
    }

    private void SeedGroup(CatalogService c, string groupId, string authority, int leafCount, int releasesPerLeaf = 2)
    {
        var leaves = new List<GroupDatCatalogLeafCreate>(leafCount);
        for (int i = 0; i < leafCount; i++)
        {
            var id  = $"{groupId}-{i:D2}";
            var rel = $"systems/c64/{id}.db";
            LeafDatDatabaseBuilder.Build(Path.Combine(_dataDir, rel), id, Games(releasesPerLeaf), null, CancellationToken.None);
            leaves.Add(new GroupDatCatalogLeafCreate
            {
                DatLine = new DatLineRecord
                {
                    Id = id, HardwareFamilyId = "c64", Name = "other", Authority = authority, MediaTypeId = "other",
                    Version = "1", DataStorePath = rel, ReleaseCount = releasesPerLeaf, ImportedAtUtc = DateTime.UtcNow,
                },
                RelativeDatPath = $"Set{i:D2}/g.dat", SourceDatName = "g.dat",
                SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
            });
        }
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = groupId, DisplayName = groupId, HardwareFamilyId = "c64", Authority = authority, Leaves = leaves,
        });
    }

    private string LeafDbPath(string groupId, int i) => Path.Combine(_dataDir, "systems", "c64", $"{groupId}-{i:D2}.db");
    private CatalogService C() => NewCatalog();

    private static bool GroupExists(CatalogService c, string gid)
        => c.GetDatGroup(Arkadia.Data.Identifiers.DatGroupId.FromPersisted(gid)) is not null;
    private static int LeafRows(CatalogService c, string gid)
        => c.LoadDatLines().Count(dl => dl.Id.StartsWith(gid + "-", StringComparison.Ordinal));

    // ── 1. Empty Group happy path ─────────────────────────────────────────────

    [Fact]
    public void EmptyGroup_Deletes_RowsAndLeafFiles()
    {
        var c = C();
        SeedGroup(c, "acornatom-custom", "custom", 3);
        Assert.True(File.Exists(LeafDbPath("acornatom-custom", 0)));

        var outcome = GroupDeleteService.Delete(c, _dataDir, _appRoot, "acornatom-custom");

        Assert.True(outcome.Deleted);
        Assert.Equal(3, outcome.LeavesDeleted);
        Assert.Empty(outcome.FileDeleteFailures);
        Assert.False(GroupExists(c, "acornatom-custom"));                 // dat_groups row gone
        Assert.Equal(0, LeafRows(c, "acornatom-custom"));                 // all dat_lines gone
        Assert.False(File.Exists(LeafDbPath("acornatom-custom", 0)));     // leaf DB files gone
        Assert.False(File.Exists(LeafDbPath("acornatom-custom", 2)));
    }

    // ── 2. Leaf-first + FK ON: a still-referenced group rolls back cleanly ─────

    [Fact]
    public void CatalogDelete_LeafFirst_FkRestrictRollsBack()
    {
        var c = C();
        SeedGroup(c, "grp", "tosec", 2);
        var leafIds = c.GetLeavesForGroup("grp").Select(l => l.DatLine.Id).ToList();
        // A group-owned volume makes the dat_groups delete FK-RESTRICT-fail → whole transaction rolls back.
        c.SaveVolume(new VolumeRecord
        {
            Id = "v1", Label = "V1", PlatformId = "c64", DatLineId = null, GroupId = "grp",
            Status = "present", PlannedSizeBytes = 0, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        });

        Assert.ThrowsAny<Exception>(() => c.DeleteDatGroupCatalog("grp", leafIds));

        Assert.True(GroupExists(c, "grp"));            // group intact
        Assert.Equal(2, LeafRows(c, "grp"));           // leaf rows restored by rollback
    }

    // ── 3. Group-owned volume blocks ──────────────────────────────────────────

    [Fact]
    public void GroupOwnedVolume_Blocks()
    {
        var c = C();
        SeedGroup(c, "grp", "tosec", 2);
        c.SaveVolume(new VolumeRecord
        {
            Id = "v1", Label = "V1", PlatformId = "c64", DatLineId = null, GroupId = "grp",
            Status = "present", PlannedSizeBytes = 0, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        });

        var outcome = GroupDeleteService.Delete(c, _dataDir, _appRoot, "grp");

        Assert.False(outcome.Deleted);
        Assert.Equal(GroupDeleteBlock.GroupOwnedVolumes, outcome.Reason);
        Assert.True(GroupExists(c, "grp"));
        Assert.Equal(2, LeafRows(c, "grp"));
        Assert.Single(c.GetVolumes());
    }

    // ── 4. Leaf volume_artifact blocks ────────────────────────────────────────

    [Fact]
    public void LeafVolumeArtifact_Blocks()
    {
        var c = C();
        SeedGroup(c, "grp", "tosec", 2);
        var leafId = c.GetLeavesForGroup("grp").First().DatLine.Id;
        c.SaveVolumeArtifact(new VolumeArtifactRecord
        {
            Id = "va1", VolumeId = "vX", DatLineId = leafId, DerivedArtifactId = "da1",
            ContentIdentityKey = "ck1", Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
        });

        var outcome = GroupDeleteService.Delete(c, _dataDir, _appRoot, "grp");

        Assert.False(outcome.Deleted);
        Assert.Equal(GroupDeleteBlock.LeafVolumeAssignments, outcome.Reason);
        Assert.True(GroupExists(c, "grp"));
        Assert.Equal(2, LeafRows(c, "grp"));
    }

    // ── 5. Present / derived leaf state blocks ────────────────────────────────

    [Fact]
    public void PresentReleaseInLeaf_Blocks()
    {
        var c = C();
        SeedGroup(c, "grp", "tosec", 2);
        var leaf = c.GetLeavesForGroup("grp").First();
        var store = new DatLineStore(Path.Combine(_dataDir, leaf.DatLine.DataStorePath));
        store.UpdateReleaseStatus(store.LoadReleases().First().Id, "present");

        var outcome = GroupDeleteService.Delete(c, _dataDir, _appRoot, "grp");

        Assert.False(outcome.Deleted);
        Assert.Equal(GroupDeleteBlock.PresentContent, outcome.Reason);
        Assert.True(GroupExists(c, "grp"));
        Assert.True(File.Exists(LeafDbPath("grp", 0)));   // leaf DB not touched
    }

    // ── 6. Staging material blocks; an empty staging dir does not ─────────────

    [Fact]
    public void StagingMaterial_Blocks_ButEmptyDirDoesNot()
    {
        var c = C();
        SeedGroup(c, "grp", "tosec", 2);
        var leafId = c.GetLeavesForGroup("grp").First().DatLine.Id;

        // Empty structural directory only → still deletable.
        Directory.CreateDirectory(Path.Combine(_appRoot, "staging", "c64", leafId));
        Assert.True(GroupDeleteService.Evaluate(c, _dataDir, _appRoot, "grp").CanDelete);

        // A real staged file → blocked.
        var stagedDir = Path.Combine(_appRoot, "staging", "c64", leafId, "Rel");
        Directory.CreateDirectory(stagedDir);
        File.WriteAllBytes(Path.Combine(stagedDir, "f.bin"), new byte[] { 1 });

        var outcome = GroupDeleteService.Delete(c, _dataDir, _appRoot, "grp");
        Assert.False(outcome.Deleted);
        Assert.Equal(GroupDeleteBlock.StagingMaterial, outcome.Reason);
        Assert.True(GroupExists(c, "grp"));
    }

    // ── 7. Cross-Group isolation ──────────────────────────────────────────────

    [Fact]
    public void DeletingGroupA_LeavesGroupB_AndSingle_Untouched()
    {
        var c = C();
        SeedGroup(c, "grp-a", "tosec", 2);
        SeedGroup(c, "grp-b", "tosec", 2);
        // A standalone Single DAT.
        LeafDatDatabaseBuilder.Build(Path.Combine(_dataDir, "systems", "c64", "single.db"), "single", Games(2), null, CancellationToken.None);
        c.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "single", HardwareFamilyId = "c64", Name = "single", Authority = "tosec", MediaTypeId = "other",
                    Version = "1", DataStorePath = "systems/c64/single.db", ReleaseCount = 2, ImportedAtUtc = DateTime.UtcNow },
        });

        var outcome = GroupDeleteService.Delete(c, _dataDir, _appRoot, "grp-a");

        Assert.True(outcome.Deleted);
        Assert.False(GroupExists(c, "grp-a"));
        Assert.True(GroupExists(c, "grp-b"));
        Assert.Equal(2, LeafRows(c, "grp-b"));
        Assert.Contains(c.LoadDatLines(), dl => dl.Id == "single");        // Single DAT untouched
        Assert.True(File.Exists(Path.Combine(_dataDir, "systems", "c64", "single.db")));
    }

    // ── 8. Missing group is a clean block, not a throw ────────────────────────

    [Fact]
    public void MissingGroup_BlocksCleanly()
    {
        var c = C();
        var outcome = GroupDeleteService.Delete(c, _dataDir, _appRoot, "does-not-exist");
        Assert.False(outcome.Deleted);
        Assert.Equal(GroupDeleteBlock.GroupNotFound, outcome.Reason);
    }

    // ── 9. Single Delete DAT regression: existing behavior (row + leaf DB file) unchanged ─────

    [Fact]
    public void SingleDeleteDatLine_RemovesRowAndFile_Unchanged()
    {
        var c = C();
        var db = Path.Combine(_dataDir, "systems", "c64", "single.db");
        LeafDatDatabaseBuilder.Build(db, "single", Games(2), null, CancellationToken.None);
        c.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "single", HardwareFamilyId = "c64", Name = "single", Authority = "tosec", MediaTypeId = "other",
                    Version = "1", DataStorePath = "systems/c64/single.db", ReleaseCount = 2, ImportedAtUtc = DateTime.UtcNow },
        });
        Assert.True(File.Exists(db));

        c.DeleteDatLine("single", "c64");

        Assert.DoesNotContain(c.LoadDatLines(), dl => dl.Id == "single");
        Assert.False(File.Exists(db));
    }
}
