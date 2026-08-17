using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Group volume Apply + LOCAL Build (V-G3): per-leaf assignments (§26), nested local materialization by File.Move
/// (§27), the whole-set pre-scan / inconsistency block, unconditional recovery finalization (§16), and the pure
/// stale-plan guard comparison (§28). Real temp catalog + leaf DBs + temp filesystem only.
/// </summary>
public sealed class GroupVolumeExecutionServiceTests : IDisposable
{
    private const string Group = "c64-tosec";
    private readonly string _dir;

    public GroupVolumeExecutionServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkGVExec_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Fixtures (mirror GroupVolumePlanningServiceTests seeding) ──────────────

    private CatalogService NewCatalog()
    {
        var c = new CatalogService(_dir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        { new() { Id = "c64", Name = "C64", Manufacturer = "Commodore", HardwareTypeId = "console" } });
        c.CreateDatGroup(
            DatGroupId.TryCreateNew(Group, out var id, out _, out _) ? id : throw new InvalidOperationException(),
            "Commodore 64 TOSEC", "c64", "tosec");
        return c;
    }

    private VolumeRecord GroupVolume(CatalogService c, long capacity = 1_000_000)
    {
        var v = new VolumeRecord
        {
            Id = "gv", Label = "c64-tosec-001", PlatformId = "c64", DatLineId = null, GroupId = Group,
            Status = "init", PlannedSizeBytes = capacity, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        };
        c.SaveVolume(v);
        return v;
    }

    private (GroupLeafRecord Leaf, string DaId, string RelPath, string FileName) SeedLeaf(
        string leafId, string relId, long size, bool writeArchive)
    {
        var dsp = $"systems/c64/{leafId}.db";
        var abs = Path.Combine(_dir, dsp);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        var store = new DatLineStore(abs);
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = leafId, Name = "Game " + relId, Status = "present" });
        var cik = "ck-" + leafId + "-" + relId;
        store.EnsureContentIdentity(new ContentIdentityRecord
        { ContentIdentityKey = cik, DatSha1 = "sha", DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });

        var fileName = $"{relId}.chd";
        var relPath  = $"archive/c64/{leafId}/{fileName}";
        var daId     = store.IngestDerivedArtifact(cik, "", "chd", fileName, relPath, size, "sha");

        if (writeArchive)
        {
            var arch = Path.Combine(_dir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(arch)!);
            File.WriteAllBytes(arch, new byte[size]);
        }

        var leaf = new GroupLeafRecord(
            new DatLineRecord { Id = leafId, HardwareFamilyId = "c64", Name = leafId + " (display)", DataStorePath = dsp },
            new DatLineGroupMetadataRecord { DatLineId = leafId, GroupId = Group });
        return (leaf, daId, relPath, fileName);
    }

    private static IReadOnlyDictionary<string, string> SegMap(params GroupLeafRecord[] leaves)
        => GroupRelativeLeafPath.BuildMap(Group, leaves.Select(l => l.DatLine.Id)).Map;

    // ── §26 Apply ──────────────────────────────────────────────────────────────

    [Fact] // Apply writes one present_in_final row per artifact, tagged with the EXACT leaf (never the GroupId)
    public void Apply_WritesPerLeafAssignments_AndRecomputesSize()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, daA, _, _) = SeedLeaf("c64-tosec-firmware-crt",     "r1", 100, writeArchive: true);
        var (b, daB, _, _) = SeedLeaf("c64-tosec-games-action-d64", "r2", 250, writeArchive: true);
        var leaves = new[] { a, b };

        var plan = GroupVolumePlanningService.Plan(c, _dir, _dir, v, leaves);
        var n    = GroupVolumeExecutionService.Apply(c, _dir, v, leaves, plan);

        Assert.Equal(2, n);
        var vas = c.GetVolumeArtifacts(v.Id);
        Assert.Equal(2, vas.Count);
        Assert.All(vas, r => Assert.Equal("present_in_final", r.Status));
        Assert.DoesNotContain(vas, r => r.DatLineId == Group);                       // never the group id
        Assert.Contains(vas, r => r.DatLineId == "c64-tosec-firmware-crt"     && r.DerivedArtifactId == daA);
        Assert.Contains(vas, r => r.DatLineId == "c64-tosec-games-action-d64" && r.DerivedArtifactId == daB);

        Assert.Equal(350, c.GetVolumeById(v.Id)!.ActualSizeBytes);                    // recomputed from DB sizes
    }

    // ── §27 Build ────────────────────────────────────────────────────────────────

    [Fact] // Build moves each archive file to volumes/<label>/<leaf-segment>/<file>, removes the source, finalizes
    public void Build_MaterializesNestedLayout_AndFinalizes()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, daA, relA, fileA) = SeedLeaf("c64-tosec-firmware-crt",     "r1", 100, writeArchive: true);
        var (b, daB, relB, fileB) = SeedLeaf("c64-tosec-games-action-d64", "r2", 250, writeArchive: true);
        var leaves = new[] { a, b };

        var plan = GroupVolumePlanningService.Plan(c, _dir, _dir, v, leaves);
        GroupVolumeExecutionService.Apply(c, _dir, v, leaves, plan);
        var res = GroupVolumeExecutionService.Build(c, _dir, _dir, v, leaves, SegMap(a, b));

        Assert.Equal(GroupBuildStatus.Built, res.Status);
        Assert.Equal(2, res.MovedCount);

        var volRoot = Path.Combine(_dir, "volumes", "c64-tosec-001");
        var dstA = Path.Combine(volRoot, "firmware-crt",     fileA);
        var dstB = Path.Combine(volRoot, "games-action-d64", fileB);
        Assert.True(File.Exists(dstA));
        Assert.True(File.Exists(dstB));
        Assert.False(File.Exists(Path.Combine(_dir, relA.Replace('/', Path.DirectorySeparatorChar))));   // source moved
        Assert.False(File.Exists(Path.Combine(_dir, relB.Replace('/', Path.DirectorySeparatorChar))));

        // Volume finalized: present + current workspace location.
        Assert.Equal("present", c.GetVolumeById(v.Id)!.Status);
        var loc = c.GetCurrentLocation(v.Id);
        Assert.NotNull(loc);
        Assert.Equal("workspace", loc!.LocationType);
        Assert.True(loc.IsCurrent);

        // Per-leaf derived state converged to present (leaf-owned).
        Assert.Equal("present", new DatLineStore(Path.Combine(_dir, a.DatLine.DataStorePath))
            .GetDerivedArtifacts().Single(d => d.Id == daA).Status);
        Assert.Equal("present", new DatLineStore(Path.Combine(_dir, b.DatLine.DataStorePath))
            .GetDerivedArtifacts().Single(d => d.Id == daB).Status);
    }

    [Fact] // §24: the same artifact filename under two different leaves builds to two distinct nested paths
    public void Build_SameFilename_DifferentLeaves_BothBuilt()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        // Identical relId → identical file name "same.chd", but different leaves.
        var (a, _, _, _) = SeedLeaf("c64-tosec-firmware-crt",     "same", 10, writeArchive: true);
        var (b, _, _, _) = SeedLeaf("c64-tosec-games-action-d64", "same", 20, writeArchive: true);
        var leaves = new[] { a, b };

        var plan = GroupVolumePlanningService.Plan(c, _dir, _dir, v, leaves);
        GroupVolumeExecutionService.Apply(c, _dir, v, leaves, plan);
        var res = GroupVolumeExecutionService.Build(c, _dir, _dir, v, leaves, SegMap(a, b));

        Assert.Equal(2, res.MovedCount);
        var volRoot = Path.Combine(_dir, "volumes", "c64-tosec-001");
        Assert.True(File.Exists(Path.Combine(volRoot, "firmware-crt",     "same.chd")));
        Assert.True(File.Exists(Path.Combine(volRoot, "games-action-d64", "same.chd")));
    }

    [Fact] // §16 recovery: a fully-already-built, consistent volume moves ZERO files but STILL finalizes
    public void Build_AllAlreadyBuilt_StillFinalizes()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, daA, relA, fileA) = SeedLeaf("c64-tosec-firmware-crt", "r1", 100, writeArchive: true);
        var leaves = new[] { a };

        var plan = GroupVolumePlanningService.Plan(c, _dir, _dir, v, leaves);
        GroupVolumeExecutionService.Apply(c, _dir, v, leaves, plan);

        // Simulate a prior Build that moved the file but crashed BEFORE finalize: dst present, source gone, init.
        var volRoot = Path.Combine(_dir, "volumes", "c64-tosec-001", "firmware-crt");
        Directory.CreateDirectory(volRoot);
        File.Move(Path.Combine(_dir, relA.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(volRoot, fileA));
        Assert.Equal("init", c.GetVolumeById(v.Id)!.Status);

        var res = GroupVolumeExecutionService.Build(c, _dir, _dir, v, leaves, SegMap(a));

        Assert.Equal(GroupBuildStatus.Built, res.Status);
        Assert.Equal(0, res.MovedCount);
        Assert.Equal(1, res.AlreadyBuiltCount);
        Assert.Equal("present", c.GetVolumeById(v.Id)!.Status);            // finalized despite zero moves
        Assert.NotNull(c.GetCurrentLocation(v.Id));
        Assert.Equal("present", new DatLineStore(Path.Combine(_dir, a.DatLine.DataStorePath))
            .GetDerivedArtifacts().Single(d => d.Id == daA).Status);
    }

    [Fact] // pre-scan blocks the ENTIRE build on an inconsistency (source AND destination present) — nothing moves
    public void Build_Inconsistent_BlocksBeforeAnyMove()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, _, _, fileA) = SeedLeaf("c64-tosec-firmware-crt",     "r1", 100, writeArchive: true);
        var (b, _, relB, _)  = SeedLeaf("c64-tosec-games-action-d64", "r2", 250, writeArchive: true);
        var leaves = new[] { a, b };

        var plan = GroupVolumePlanningService.Plan(c, _dir, _dir, v, leaves);
        GroupVolumeExecutionService.Apply(c, _dir, v, leaves, plan);

        // Make leaf-a inconsistent: create its destination while its source still exists.
        var dstDirA = Path.Combine(_dir, "volumes", "c64-tosec-001", "firmware-crt");
        Directory.CreateDirectory(dstDirA);
        File.WriteAllBytes(Path.Combine(dstDirA, fileA), new byte[100]);

        Assert.Throws<InvalidOperationException>(() =>
            GroupVolumeExecutionService.Build(c, _dir, _dir, v, leaves, SegMap(a, b)));

        // leaf-b's source was NOT moved (an inconsistency in one leaf blocks earlier leaves too).
        Assert.True(File.Exists(Path.Combine(_dir, relB.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(_dir, "volumes", "c64-tosec-001", "games-action-d64", "r2.chd")));
        Assert.Equal("init", c.GetVolumeById(v.Id)!.Status);   // not finalized
    }

    // ── §28 stale-plan guard (pure comparison) ────────────────────────────────

    [Fact] // identical included sets match; an included set that shrank does not
    public void IncludedSetMatches_DetectsChange()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, daA, _, _) = SeedLeaf("c64-tosec-firmware-crt",     "r1", 100, writeArchive: true);
        var (b, _,   _, _) = SeedLeaf("c64-tosec-games-action-d64", "r2", 250, writeArchive: true);
        var leaves = new[] { a, b };

        var planned = GroupVolumePlanningService.Plan(c, _dir, _dir, v, leaves);
        Assert.True(GroupVolumeExecutionService.IncludedSetMatches(planned, planned));

        // Assign leaf-a's artifact elsewhere → it drops out of a fresh plan → included set changed.
        c.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = "other", DatLineId = "c64-tosec-firmware-crt",
                    DerivedArtifactId = daA, ContentIdentityKey = "ck-c64-tosec-firmware-crt-r1",
                    Status = "present_in_final", AddedAtUtc = DateTime.UtcNow },
        });
        var fresh = GroupVolumePlanningService.Plan(c, _dir, _dir, v, leaves);

        Assert.False(GroupVolumeExecutionService.IncludedSetMatches(planned, fresh));
    }
}
