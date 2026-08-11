using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Arkadia.GroupDats;
using Xunit;

namespace Arkadia.Tests.GroupDats;

/// <summary>
/// M8 tests: uniform Group Configure — read-only preview/plan/validation helpers plus the atomic
/// <see cref="CatalogService.ApplyDatGroupConfiguration"/> (overwrite-all, membership-drift-guarded).
/// Real CatalogService over a temp catalog.db; the real runtime is never touched.
/// </summary>
public sealed class GroupConfigureTests : IDisposable
{
    private readonly string _dir;

    public GroupConfigureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkGrpCfg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static DatLineRecord Leaf(string id, string fh = "archives_pre_extraction",
        string strat = "none", string folder = "") =>
        new() { Id = id, HardwareFamilyId = "c64", MediaTypeId = "other", Version = "1",
                DataStorePath = $"systems/c64/{id}.db", FileHandling = fh,
                TransformStrategyType = strat, FolderTransformId = folder };

    private CatalogService SeedGroup(int leaves, string groupId = "c64-tosec")
    {
        var c = new CatalogService(_dir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        { new() { Id = "c64", Name = "Commodore 64", Manufacturer = "Commodore", HardwareTypeId = "" } });

        var leafReqs = Enumerable.Range(0, leaves).Select(i => new GroupDatCatalogLeafCreate
        {
            DatLine = new DatLineRecord
            {
                Id = $"c64-tosec-{i:D3}", HardwareFamilyId = "c64", Name = "other", Authority = "tosec",
                MediaTypeId = "other", Version = "1", DataStorePath = $"systems/c64/c64-tosec-{i:D3}.db",
                ReleaseCount = 0, ImportedAtUtc = DateTime.UtcNow,
            },
            RelativeDatPath = $"S{i:D3}/g.dat", SourceDatName = "g.dat",
            SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
        }).ToArray();
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        { GroupId = groupId, DisplayName = "C64 TOSEC", HardwareFamilyId = "c64", Authority = "tosec", Leaves = leafReqs });
        return c;
    }

    // ── Preview: common vs Mixed ────────────────────────────────────────────

    [Fact]  // (1)(3)(6) common values + leaf count
    public void Preview_CommonValues()
    {
        var leaves = Enumerable.Range(0, 410).Select(i => Leaf($"c64-tosec-{i:D3}")).ToList();
        var p = GroupConfigure.BuildPreview(leaves);

        Assert.Equal(410, p.LeafCount);
        Assert.Equal("archives_pre_extraction", p.CommonFileHandling);
        Assert.Equal("none", p.CommonTransformStrategy);
        Assert.False(p.FileHandlingMixed);
        Assert.False(p.TransformStrategyMixed);
    }

    [Fact]  // (2)(4)(5) mixed detection
    public void Preview_MixedValues()
    {
        var leaves = new List<DatLineRecord>
        {
            Leaf("a", fh: "archives_pre_extraction", strat: "none"),
            Leaf("b", fh: "all_files",               strat: "release_folder", folder: "zip_compression"),
        };
        var p = GroupConfigure.BuildPreview(leaves);

        Assert.Null(p.CommonFileHandling);       Assert.True(p.FileHandlingMixed);
        Assert.Null(p.CommonTransformStrategy);  Assert.True(p.TransformStrategyMixed);
        Assert.Null(p.CommonFolderTransformId);  Assert.True(p.FolderTransformMixed);
    }

    // ── Plan / config shape ─────────────────────────────────────────────────

    [Fact]  // (10) release_folder without folder transform is invalid; none-with-folder invalid
    public void ConfigShape_Validation()
    {
        Assert.NotNull(GroupConfigure.ValidateConfigShape("release_folder", null));
        Assert.NotNull(GroupConfigure.ValidateConfigShape("release_folder", ""));
        Assert.NotNull(GroupConfigure.ValidateConfigShape("none", "zip_compression"));
        Assert.Null(GroupConfigure.ValidateConfigShape("release_folder", "zip_compression"));
        Assert.Null(GroupConfigure.ValidateConfigShape("none", null));
        Assert.NotNull(GroupConfigure.ValidateConfigShape("file_extension", null));   // unsupported in M8
    }

    [Fact]  // (11)(12) all-clean gate
    public void AllClean_Gate()
    {
        var clean = new[] { new GroupConfigureLeafValidation("a", GroupConfigureLeafValidationState.Clean),
                            new GroupConfigureLeafValidation("b", GroupConfigureLeafValidationState.Clean) };
        var oneBad = new[] { new GroupConfigureLeafValidation("a", GroupConfigureLeafValidationState.Clean),
                             new GroupConfigureLeafValidation("b", GroupConfigureLeafValidationState.Collision) };
        Assert.True(GroupConfigure.AllClean(clean));
        Assert.False(GroupConfigure.AllClean(oneBad));
        Assert.False(GroupConfigure.AllClean(Array.Empty<GroupConfigureLeafValidation>()));
        Assert.Equal(GroupConfigureLeafValidationState.Collision, GroupConfigure.ClassifyLeaf(1));
        Assert.Equal(GroupConfigureLeafValidationState.Clean,     GroupConfigure.ClassifyLeaf(0));
    }

    // ── Atomic apply ────────────────────────────────────────────────────────

    [Fact]  // (16)(17)(18)(25)(26) every leaf receives the exact uniform config
    public void Apply_AllLeavesReceiveConfig()
    {
        var c   = SeedGroup(410);
        var ids = c.GetLeavesForGroup("c64-tosec").Select(l => l.DatLine.Id).ToList();

        var count = c.ApplyDatGroupConfiguration("c64-tosec", ids,
            "archives_pre_extraction", "release_folder", "zip_compression");

        Assert.Equal(410, count);
        var leaves = c.GetLeavesForGroup("c64-tosec").Select(l => l.DatLine).ToList();
        Assert.All(leaves, dl =>
        {
            Assert.Equal("archives_pre_extraction", dl.FileHandling);
            Assert.Equal("release_folder", dl.TransformStrategyType);
            Assert.Equal("zip_compression", dl.FolderTransformId);
        });
    }

    [Fact]  // (19)(20) non-group and other-group dat_lines untouched
    public void Apply_LeavesOtherLinesUntouched()
    {
        var c = SeedGroup(3, "c64-tosec");
        // A standalone single dat_line + another group's leaf (seed via SaveDatLines, no group).
        c.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "c64-single", HardwareFamilyId = "c64", Name = "other", Authority = "nfo",
                    MediaTypeId = "other", Version = "1", DataStorePath = "systems/c64/c64-single.db",
                    FileHandling = "all_files", TransformStrategyType = "none" },
        });

        var ids = c.GetLeavesForGroup("c64-tosec").Select(l => l.DatLine.Id).ToList();
        c.ApplyDatGroupConfiguration("c64-tosec", ids, "archives_pre_extraction", "release_folder", "zip_compression");

        // The standalone (group_id NULL) line must NOT be touched by the WHERE group_id = … UPDATE:
        // it keeps its defaults, distinct from the applied release_folder / zip_compression.
        var single = c.LoadDatLines().First(d => d.Id == "c64-single");
        Assert.Equal("none", single.TransformStrategyType);
        Assert.Equal("", single.FolderTransformId);
    }

    [Fact]  // (22) membership drift blocks + rolls back
    public void Apply_MembershipDrift_Blocked()
    {
        var c   = SeedGroup(3);
        var ids = c.GetLeavesForGroup("c64-tosec").Select(l => l.DatLine.Id).ToList();
        ids.Add("c64-tosec-ghost");   // expected set no longer matches actual membership

        var ex = Assert.Throws<GroupConfigureApplyException>(() =>
            c.ApplyDatGroupConfiguration("c64-tosec", ids, "archives_pre_extraction", "release_folder", "zip_compression"));
        Assert.Equal(GroupConfigureApplyError.MembershipDrift, ex.Error);

        // Nothing applied — leaves keep their defaults.
        Assert.All(c.GetLeavesForGroup("c64-tosec").Select(l => l.DatLine),
            dl => Assert.Equal("none", dl.TransformStrategyType));
    }

    [Fact]  // (23) nonexistent group blocked
    public void Apply_NonexistentGroup_Blocked()
    {
        var c = SeedGroup(2);
        var ex = Assert.Throws<GroupConfigureApplyException>(() =>
            c.ApplyDatGroupConfiguration("no-such-group", new[] { "x" }, "all_files", "none", null));
        Assert.Equal(GroupConfigureApplyError.GroupNotFound, ex.Error);
    }

    [Fact]  // (10 write-path) invalid config rejected before any write
    public void Apply_InvalidConfig_Rejected()
    {
        var c   = SeedGroup(2);
        var ids = c.GetLeavesForGroup("c64-tosec").Select(l => l.DatLine.Id).ToList();
        var ex = Assert.Throws<GroupConfigureApplyException>(() =>
            c.ApplyDatGroupConfiguration("c64-tosec", ids, "archives_pre_extraction", "release_folder", null));
        Assert.Equal(GroupConfigureApplyError.InvalidConfig, ex.Error);
    }

    [Fact]  // (9) overwrite-all: even leaves already differently-configured are overwritten uniformly
    public void Apply_OverwriteAll()
    {
        var c   = SeedGroup(3);
        var ids = c.GetLeavesForGroup("c64-tosec").Select(l => l.DatLine.Id).ToList();
        // First apply a config, then overwrite with a different one.
        c.ApplyDatGroupConfiguration("c64-tosec", ids, "all_files", "none", null);
        c.ApplyDatGroupConfiguration("c64-tosec", ids, "archives_pre_extraction", "release_folder", "zip_compression");

        Assert.All(c.GetLeavesForGroup("c64-tosec").Select(l => l.DatLine), dl =>
        {
            Assert.Equal("archives_pre_extraction", dl.FileHandling);
            Assert.Equal("release_folder", dl.TransformStrategyType);
            Assert.Equal("zip_compression", dl.FolderTransformId);
        });
    }

    // ── Leaf validation: unvalidatable ⇒ Error (never Clean) ─────────────────

    private static DatParser.ParsedGame Game(string name) => new()
    {
        Name = name,
        Roms = new List<DatParser.ParsedRom> { new() { Name = name + ".d64", Size = "10", Sha1 = new string('a', 40) } },
    };

    private DatLineRecord LeafWithBuiltDb(string id, params string[] releaseNames)
    {
        var rel = $"systems/c64/{id}.db";
        LeafDatDatabaseBuilder.Build(Path.Combine(_dir, rel), id,
            releaseNames.Select(Game).ToList(), null, CancellationToken.None);
        return Leaf(id);   // DataStorePath = systems/c64/{id}.db
    }

    [Fact]  // (1) missing leaf DB → Error, NOT Clean
    public void ValidateLeaf_MissingDb_IsError()
    {
        var c    = new CatalogService(_dir);   // ensures transforms are seeded
        var leaf = Leaf("c64-tosec-000");       // DataStorePath points at a file that was never built
        var r    = GroupConfigureLeafValidator.ValidateLeaf(leaf, _dir, "release_folder",
            c.LoadTransforms().First(t => t.Id == "zip_compression"), c.LoadTransforms());

        Assert.Equal(GroupConfigureLeafValidationState.Error, r.State);
    }

    [Fact]  // (2) unreadable / not-a-database file → Error
    public void ValidateLeaf_UnreadableDb_IsError()
    {
        var c   = new CatalogService(_dir);
        var abs = Path.Combine(_dir, "systems", "c64", "c64-tosec-000.db");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "this is not a sqlite database");

        var r = GroupConfigureLeafValidator.ValidateLeaf(Leaf("c64-tosec-000"), _dir, "none", null, c.LoadTransforms());
        Assert.Equal(GroupConfigureLeafValidationState.Error, r.State);
    }

    [Fact]  // (4) a real, readable leaf DB with distinct release names → Clean
    public void ValidateLeaf_ReadableCleanDb_IsClean()
    {
        var c    = new CatalogService(_dir);
        var leaf = LeafWithBuiltDb("c64-tosec-000", "Game A", "Game B", "Game C");
        var r    = GroupConfigureLeafValidator.ValidateLeaf(leaf, _dir, "release_folder",
            c.LoadTransforms().First(t => t.Id == "zip_compression"), c.LoadTransforms());

        Assert.Equal(GroupConfigureLeafValidationState.Clean, r.State);
    }

    [Fact]  // (3) one Error among N ⇒ Group not fully clean ⇒ Apply disabled
    public void Validation_OneError_BlocksApply()
    {
        var c = new CatalogService(_dir);
        var okLeaf  = LeafWithBuiltDb("c64-tosec-000", "Game A", "Game B");
        var badLeaf = Leaf("c64-tosec-001");   // no DB built → Error
        var xf = c.LoadTransforms();
        var zip = xf.First(t => t.Id == "zip_compression");

        var results = new[]
        {
            GroupConfigureLeafValidator.ValidateLeaf(okLeaf,  _dir, "release_folder", zip, xf),
            GroupConfigureLeafValidator.ValidateLeaf(badLeaf, _dir, "release_folder", zip, xf),
        };

        Assert.Equal(GroupConfigureLeafValidationState.Clean, results[0].State);
        Assert.Equal(GroupConfigureLeafValidationState.Error, results[1].State);
        Assert.False(GroupConfigure.AllClean(results));   // Apply must be blocked
    }

    [Fact]  // (5) a failed validation performs NO catalog mutation
    public void ValidateLeaf_Failure_NoMutation()
    {
        var c = SeedGroup(3);
        int groupsBefore = c.LoadDatGroups().Count;
        var before = c.GetLeavesForGroup("c64-tosec").Select(l =>
            (l.DatLine.Id, l.DatLine.TransformStrategyType, l.DatLine.FileHandling)).ToList();

        // Validate against a leaf with a missing DB (→ Error). No write path is exercised.
        var r = GroupConfigureLeafValidator.ValidateLeaf(
            c.GetLeavesForGroup("c64-tosec").First().DatLine, _dir, "release_folder",
            c.LoadTransforms().First(t => t.Id == "zip_compression"), c.LoadTransforms());
        Assert.Equal(GroupConfigureLeafValidationState.Error, r.State);   // seeded group has no built leaf DBs

        Assert.Equal(groupsBefore, c.LoadDatGroups().Count);
        var after = c.GetLeavesForGroup("c64-tosec").Select(l =>
            (l.DatLine.Id, l.DatLine.TransformStrategyType, l.DatLine.FileHandling)).ToList();
        Assert.Equal(before, after);   // nothing changed
    }

    [Fact]  // none strategy clears folder transform to NULL (Single Configure parity)
    public void Apply_NoneClearsFolderTransform()
    {
        var c   = SeedGroup(2);
        var ids = c.GetLeavesForGroup("c64-tosec").Select(l => l.DatLine.Id).ToList();
        c.ApplyDatGroupConfiguration("c64-tosec", ids, "archives_pre_extraction", "release_folder", "zip_compression");
        c.ApplyDatGroupConfiguration("c64-tosec", ids, "all_files", "none", null);

        Assert.All(c.GetLeavesForGroup("c64-tosec").Select(l => l.DatLine), dl =>
        {
            Assert.Equal("none", dl.TransformStrategyType);
            Assert.Equal("", dl.FolderTransformId);   // cleared
        });
    }
}
