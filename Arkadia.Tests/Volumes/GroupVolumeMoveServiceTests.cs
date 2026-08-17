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
/// Group Move Volume expected-hash snapshot (V-G4, §23/§4/§19): expected hashes are loaded from the EXACT leaf
/// stores named by <c>volume_artifacts.dat_line_id</c> (never the GroupId), a Group volume may carry assignments
/// from multiple leaves, and any unreconcilable leaf/artifact blocks BEFORE copy. Real temp catalog + leaf DBs;
/// no physical move.
/// </summary>
public sealed class GroupVolumeMoveServiceTests : IDisposable
{
    private const string Group = "c64-tosec";
    private readonly string _dir;

    public GroupVolumeMoveServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkGVMoveSvc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

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

    private VolumeRecord GroupVolume(CatalogService c)
    {
        var v = new VolumeRecord
        {
            Id = "gv", Label = "c64-tosec-001", PlatformId = "c64", DatLineId = null, GroupId = Group,
            Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        };
        c.SaveVolume(v);
        return v;
    }

    // Seeds a leaf DB with one release/artifact and returns its GroupLeafRecord + (daId, sha1, fileName).
    private (GroupLeafRecord Leaf, string DaId, string Sha1, string FileName) SeedLeaf(
        string leafId, string relId, long size, string fileName, string sha1)
    {
        var dsp = $"systems/c64/{leafId}.db";
        var abs = Path.Combine(_dir, dsp);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        var store = new DatLineStore(abs);
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = leafId, Name = "Game " + relId, Status = "present" });
        var cik = "ck-" + leafId + "-" + relId;
        store.EnsureContentIdentity(new ContentIdentityRecord
        { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });

        var relPath = $"archive/c64/{leafId}/{fileName}";
        var daId    = store.IngestDerivedArtifact(cik, "", "chd", fileName, relPath, size, sha1);

        var leaf = new GroupLeafRecord(
            new DatLineRecord { Id = leafId, HardwareFamilyId = "c64", Name = leafId + " (display)", DataStorePath = dsp },
            new DatLineGroupMetadataRecord { DatLineId = leafId, GroupId = Group });
        return (leaf, daId, sha1, fileName);
    }

    private void Assign(CatalogService c, VolumeRecord v, string leafId, string daId)
        => c.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = v.Id, DatLineId = leafId,
                    DerivedArtifactId = daId, ContentIdentityKey = "ck-" + leafId,
                    Status = "present_in_final", AddedAtUtc = DateTime.UtcNow },
        });

    // ── §23: multi-leaf snapshot; hashes from the correct leaf stores; GroupId never substituted ──

    [Fact]
    public void Build_MultiLeaf_LoadsHashesFromCorrectLeaves()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var shaA = new string('a', 40);
        var shaB = new string('b', 40);
        var (a, daA, _, _) = SeedLeaf("c64-tosec-firmware-crt",     "r1", 100, "game.zip", shaA);
        var (b, daB, _, _) = SeedLeaf("c64-tosec-games-action-d64", "r2", 250, "game.zip", shaB);
        Assign(c, v, a.DatLine.Id, daA);
        Assign(c, v, b.DatLine.Id, daB);

        var snap = GroupVolumeMoveService.Build(c, _dir, v, new[] { a, b });

        Assert.Null(snap.BlockReason);
        Assert.Equal(2, snap.Files.Count);
        // Nested relative-path identity, each hash from its own leaf store.
        Assert.Equal(shaA, snap.ExpectedSha1ByRelativePath["firmware-crt/game.zip"]);
        Assert.Equal(shaB, snap.ExpectedSha1ByRelativePath["games-action-d64/game.zip"]);
        // The exact leaf DatLineId is authoritative — the GroupId is never used as an owner.
        Assert.Contains(snap.Files, f => f.DatLineId == "c64-tosec-firmware-crt");
        Assert.Contains(snap.Files, f => f.DatLineId == "c64-tosec-games-action-d64");
        Assert.DoesNotContain(snap.Files, f => f.DatLineId == Group);
    }

    // ── §19: an assigned leaf missing from current membership blocks before copy (never dropped) ──

    [Fact]
    public void Build_AssignedLeafNotInMembership_Blocks()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, daA, _, _) = SeedLeaf("c64-tosec-firmware-crt", "r1", 100, "game.zip", new string('a', 40));
        Assign(c, v, a.DatLine.Id, daA);

        // Membership passed to Build does NOT include the assigned leaf.
        var snap = GroupVolumeMoveService.Build(c, _dir, v, Array.Empty<GroupLeafRecord>());

        Assert.NotNull(snap.BlockReason);
        Assert.Contains("c64-tosec-firmware-crt", snap.BlockReason!);
        Assert.Empty(snap.ExpectedSha1ByRelativePath);
    }

    // ── §23.5: an unavailable leaf DB blocks before copy ──

    [Fact]
    public void Build_MissingLeafDb_Blocks()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, daA, _, _) = SeedLeaf("c64-tosec-firmware-crt", "r1", 100, "game.zip", new string('a', 40));
        Assign(c, v, a.DatLine.Id, daA);

        // Delete the leaf DB after assignment.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(_dir, a.DatLine.DataStorePath));

        var snap = GroupVolumeMoveService.Build(c, _dir, v, new[] { a });

        Assert.NotNull(snap.BlockReason);
        Assert.Contains("c64-tosec-firmware-crt", snap.BlockReason!);
    }

    // ── §23.6/§4: an unresolved assigned DerivedArtifactId blocks before copy ──

    [Fact]
    public void Build_UnresolvedDerivedArtifact_Blocks()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, _, _, _) = SeedLeaf("c64-tosec-firmware-crt", "r1", 100, "game.zip", new string('a', 40));
        Assign(c, v, a.DatLine.Id, "da-does-not-exist");   // assign a phantom artifact id

        var snap = GroupVolumeMoveService.Build(c, _dir, v, new[] { a });

        Assert.NotNull(snap.BlockReason);
        Assert.Contains("da-does-not-exist", snap.BlockReason!);
    }

    // ── §24.3: two assigned artifacts mapping to the same relative path block ──

    [Fact]
    public void Build_DuplicateRelativePathWithinLeaf_Blocks()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);

        // One leaf, two releases whose derived artifacts share the same file name → same relative path.
        var dsp = "systems/c64/c64-tosec-firmware-crt.db";
        var abs = Path.Combine(_dir, dsp);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var store = new DatLineStore(abs);

        string Ingest(string relId)
        {
            store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = "c64-tosec-firmware-crt", Name = relId, Status = "present" });
            var cik = "ck-" + relId;
            store.EnsureContentIdentity(new ContentIdentityRecord
            { ContentIdentityKey = cik, DatSha1 = new string('a', 40), DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
            store.SaveReleaseContentLink(new ReleaseContentLinkRecord
            { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });
            return store.IngestDerivedArtifact(cik, "", "chd", "same.zip", $"archive/c64/x/{relId}/same.zip", 10, new string('a', 40));
        }
        var da1 = Ingest("r1");
        var da2 = Ingest("r2");

        var leaf = new GroupLeafRecord(
            new DatLineRecord { Id = "c64-tosec-firmware-crt", HardwareFamilyId = "c64", Name = "fw", DataStorePath = dsp },
            new DatLineGroupMetadataRecord { DatLineId = "c64-tosec-firmware-crt", GroupId = Group });
        Assign(c, v, "c64-tosec-firmware-crt", da1);
        Assign(c, v, "c64-tosec-firmware-crt", da2);

        var snap = GroupVolumeMoveService.Build(c, _dir, v, new[] { leaf });

        Assert.NotNull(snap.BlockReason);
        Assert.Contains("firmware-crt/same.zip", snap.BlockReason!);
    }
}
