using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Group Append (V-G6): global first-fit planning threaded across leaves in DatLineId Ordinal, then leaf-scoped
/// execution through the unchanged <see cref="AppendVolumeService"/>. Proves exact leaf-owner tagging (never the
/// GroupId), nested destination layout, same-filename-across-leaves validity, one global capacity budget, and the
/// preservation copy→verify→commit→delete-source order via the real executor. Real temp catalog + leaf DBs +
/// physical archive/group roots; no UI.
/// </summary>
public sealed class GroupAppendPlanningServiceTests : IDisposable
{
    private const string Group = "c64-tosec";
    private readonly string _appRoot;   // archive/ + volumes/ live here
    private readonly string _dataDir;   // catalog + leaf DBs

    public GroupAppendPlanningServiceTests()
    {
        _appRoot = Path.Combine(Path.GetTempPath(), "ArkGVAppend_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_appRoot, "data");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_appRoot, true); } catch { }
    }

    private static string Sha1Hex(byte[] b) => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();

    private string GroupRoot(string label) => Path.Combine(_appRoot, "volumes", label);

    private CatalogService NewCatalog()
    {
        var c = new CatalogService(_dataDir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        { new() { Id = "c64", Name = "C64", Manufacturer = "Commodore", HardwareTypeId = "console" } });
        c.CreateDatGroup(
            DatGroupId.TryCreateNew(Group, out var id, out _, out _) ? id : throw new InvalidOperationException(),
            "Commodore 64 TOSEC", "c64", "tosec");
        return c;
    }

    private VolumeRecord GroupVolume(CatalogService c, long planned, long actual = 0)
    {
        var v = new VolumeRecord
        {
            Id = "gv", Label = "c64-tosec-001", PlatformId = "c64", DatLineId = null, GroupId = Group,
            Status = "present", Health = "ok", PlannedSizeBytes = planned, ActualSizeBytes = actual,
            CreatedAt = DateTime.UtcNow,
        };
        c.SaveVolume(v);
        Directory.CreateDirectory(GroupRoot(v.Label));   // group root reachable
        return v;
    }

    // Seeds a leaf DB with one WANTED, UNASSIGNED artifact and writes its archive file under appRoot.
    private (GroupLeafRecord Leaf, string DaId, string FileName, byte[] Content) SeedLeafArtifact(
        string leafId, string relId, string fileName, byte[] content)
    {
        var dsp = $"systems/c64/{leafId}.db";
        var abs = Path.Combine(_dataDir, dsp);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var sha1 = Sha1Hex(content);
        var cik  = "sha1:" + sha1 + ":" + leafId;

        var store = new DatLineStore(abs);
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = leafId, Name = "Game " + relId, Status = "present", ReleaseContentKey = cik });
        store.EnsureContentIdentity(new ContentIdentityRecord
        { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });

        var relPath = $"archive/c64/{leafId}/{fileName}";
        var daId    = store.IngestDerivedArtifact(cik, "", "no_compression", fileName, relPath, content.Length, sha1);

        var archive = Path.Combine(_appRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.WriteAllBytes(archive, content);

        var leaf = new GroupLeafRecord(
            new DatLineRecord { Id = leafId, HardwareFamilyId = "c64", Name = leafId + " (display)", DataStorePath = dsp },
            new DatLineGroupMetadataRecord { DatLineId = leafId, GroupId = Group });
        return (leaf, daId, fileName, content);
    }

    private GroupAppendVolumePlan Plan(CatalogService c, VolumeRecord v, params GroupLeafRecord[] leaves)
        => GroupAppendPlanningService.Plan(c, _dataDir, _appRoot, v, GroupRoot(v.Label), "Commodore 64 TOSEC", leaves);

    // ── Global budget across leaves + leaf owner tagging + nested layout ──

    [Fact]
    public void Plan_ThreadsGlobalBudget_AcrossLeaves_Ordinal()
    {
        var c  = NewCatalog();
        var ca = new byte[6]; new Random(1).NextBytes(ca);
        var cb = new byte[6]; new Random(2).NextBytes(cb);
        var (a, _, _, _) = SeedLeafArtifact("c64-tosec-firmware-crt",     "r1", "a.rom", ca);
        var (b, _, _, _) = SeedLeafArtifact("c64-tosec-games-action-d64", "r2", "b.rom", cb);
        // Capacity fits only ONE 6-byte artifact.
        var v = GroupVolume(c, planned: 6);

        var plan = Plan(c, v, a, b);

        Assert.Null(plan.BlockReason);
        Assert.Equal(1, plan.Aggregate.PlannedCount);                 // global budget, not per-leaf
        Assert.True(plan.Aggregate.PlannedBytes <= 6);

        // Ordinal order: firmware-crt plans first and consumes the budget; games-action gets TooLarge.
        var pa = plan.LeafPlans.Single(p => p.DatLineId == "c64-tosec-firmware-crt");
        var pb = plan.LeafPlans.Single(p => p.DatLineId == "c64-tosec-games-action-d64");
        Assert.Equal(1, pa.PlannedCount);
        Assert.Equal(0, pb.PlannedCount);
        Assert.Equal(1, pb.TooLargeSkipped);
    }

    [Fact]
    public void Plan_LeafOwner_IsExactDatLineId_NeverGroupId()
    {
        var c = NewCatalog();
        var (a, _, _, _) = SeedLeafArtifact("c64-tosec-firmware-crt", "r1", "a.rom", new byte[] { 1, 2, 3 });
        var v = GroupVolume(c, planned: 1_000);

        var plan = Plan(c, v, a);

        var entry = plan.LeafPlans.Single(p => p.PlannedCount > 0);
        Assert.Equal("c64-tosec-firmware-crt", entry.DatLineId);
        Assert.NotEqual(Group, entry.DatLineId);
        // Nested destination: <groupRoot>/firmware-crt/a.rom (segment, not the full leaf id).
        Assert.Equal(Path.Combine(GroupRoot(v.Label), "firmware-crt", "a.rom"),
                     entry.Entries.Single(e => e.Action == AppendEntryAction.Copy).TargetPath);
    }

    [Fact]
    public void Plan_SameFilename_DifferentLeaves_BothPlanned_DistinctNestedPaths()
    {
        var c = NewCatalog();
        var (a, _, _, _) = SeedLeafArtifact("c64-tosec-firmware-crt",     "r1", "game.rom", new byte[] { 1 });
        var (b, _, _, _) = SeedLeafArtifact("c64-tosec-games-action-d64", "r2", "game.rom", new byte[] { 2, 2 });
        var v = GroupVolume(c, planned: 1_000);

        var plan = Plan(c, v, a, b);

        Assert.Equal(2, plan.Aggregate.PlannedCount);
        var ta = plan.LeafPlans.Single(p => p.DatLineId == "c64-tosec-firmware-crt")
                     .Entries.Single(e => e.Action == AppendEntryAction.Copy).TargetPath;
        var tb = plan.LeafPlans.Single(p => p.DatLineId == "c64-tosec-games-action-d64")
                     .Entries.Single(e => e.Action == AppendEntryAction.Copy).TargetPath;
        Assert.NotEqual(ta, tb);
        Assert.Equal(Path.Combine(GroupRoot(v.Label), "firmware-crt", "game.rom"), ta);
        Assert.Equal(Path.Combine(GroupRoot(v.Label), "games-action-d64", "game.rom"), tb);
    }

    [Fact]
    public void Plan_MissingLeafDb_Blocks()
    {
        var c = NewCatalog();
        var (a, _, _, _) = SeedLeafArtifact("c64-tosec-firmware-crt", "r1", "a.rom", new byte[] { 1, 2, 3 });
        var v = GroupVolume(c, planned: 1_000);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(_dataDir, a.DatLine.DataStorePath));

        var plan = Plan(c, v, a);

        Assert.NotNull(plan.BlockReason);
        Assert.Contains("c64-tosec-firmware-crt", plan.BlockReason!);
    }

    // ── Execution (leaf-scoped, via the unchanged AppendVolumeService) ──

    [Fact]
    public void Execute_LeafScoped_WritesLeafOwner_NestedFile_DeletesArchiveSource()
    {
        var c = NewCatalog();
        var ca = new byte[] { 10, 20, 30 };
        var cb = new byte[] { 40, 50, 60, 70 };
        var (a, daA, _, _) = SeedLeafArtifact("c64-tosec-firmware-crt",     "r1", "a.rom", ca);
        var (b, daB, _, _) = SeedLeafArtifact("c64-tosec-games-action-d64", "r2", "b.rom", cb);
        var v = GroupVolume(c, planned: 1_000);

        var plan = Plan(c, v, a, b);
        Assert.Null(plan.BlockReason);

        var svc = new AppendVolumeService(c);
        foreach (var leafPlan in plan.LeafPlans.Where(p => p.PlannedCount > 0))
            svc.Execute(leafPlan);

        // Files materialized at nested leaf paths; archive sources removed (Append = verified move).
        Assert.True(File.Exists(Path.Combine(GroupRoot(v.Label), "firmware-crt", "a.rom")));
        Assert.True(File.Exists(Path.Combine(GroupRoot(v.Label), "games-action-d64", "b.rom")));
        Assert.False(File.Exists(Path.Combine(_appRoot, "archive", "c64", "c64-tosec-firmware-crt", "a.rom")));
        Assert.False(File.Exists(Path.Combine(_appRoot, "archive", "c64", "c64-tosec-games-action-d64", "b.rom")));

        // volume_artifacts rows carry the EXACT leaf owner, never the GroupId.
        var vas = c.GetVolumeArtifacts(v.Id);
        Assert.Equal(2, vas.Count);
        Assert.Contains(vas, x => x.DatLineId == "c64-tosec-firmware-crt"     && x.DerivedArtifactId == daA);
        Assert.Contains(vas, x => x.DatLineId == "c64-tosec-games-action-d64" && x.DerivedArtifactId == daB);
        Assert.DoesNotContain(vas, x => x.DatLineId == Group);
        Assert.Equal(ca.Length + cb.Length, c.GetVolumeById(v.Id)!.ActualSizeBytes);
    }

    [Fact] // preservation: a bad archive source (hash mismatch) leaves the archive intact and writes no assignment
    public void Execute_VerifyFailure_KeepsArchiveSource_NoAssignment()
    {
        var c = NewCatalog();
        var (a, _, _, _) = SeedLeafArtifact("c64-tosec-firmware-crt", "r1", "a.rom", new byte[] { 1, 2, 3, 4 });
        var v = GroupVolume(c, planned: 1_000);
        var plan = Plan(c, v, a);

        // Corrupt the archive source so the destination SHA1 won't match the DB hash.
        var archive = Path.Combine(_appRoot, "archive", "c64", "c64-tosec-firmware-crt", "a.rom");
        File.WriteAllBytes(archive, new byte[] { 9, 9, 9, 9 });

        var svc = new AppendVolumeService(c);
        var res = svc.Execute(plan.LeafPlans.Single(p => p.PlannedCount > 0));

        Assert.Equal(0, res.CopiedCount);
        Assert.True(res.ErrorCount > 0);
        Assert.True(File.Exists(archive));                          // source retained
        Assert.False(File.Exists(Path.Combine(GroupRoot(v.Label), "firmware-crt", "a.rom")));  // bad dst removed
        Assert.Empty(c.GetVolumeArtifacts(v.Id));                   // no assignment committed
    }
}
