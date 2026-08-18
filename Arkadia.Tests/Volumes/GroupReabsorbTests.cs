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
/// Group Reabsorb (V-G8): the read-only Group candidate builder + the shared execution/finalization/lifecycle
/// service reused by Single and Group. Proves leaf-nested source paths, DB-authoritative archive destinations
/// (never a Group archive layout), exact per-leaf ownership + finalization, full-success deletes only the Group
/// volume (Group/leaf DBs survive), partial retention, and the empty-volume no-op. Real temp catalog + leaf DBs +
/// physical roots; no UI.
/// </summary>
public sealed class GroupReabsorbTests : IDisposable
{
    private const string Group = "c64-tosec";
    private readonly string _appRoot;
    private readonly string _dataDir;

    public GroupReabsorbTests()
    {
        _appRoot = Path.Combine(Path.GetTempPath(), "ArkGVReab_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_appRoot, "data");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_appRoot, true); } catch { }
    }

    private static string Sha1Hex(byte[] b) => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();
    private string VolRoot(string label) => Path.Combine(_appRoot, "volumes", label);

    private CatalogService NewCatalog()
    {
        var c = new CatalogService(_dataDir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        { new() { Id = "c64", Name = "C64", Manufacturer = "Commodore", HardwareTypeId = "console" } });
        c.CreateDatGroup(DatGroupId.TryCreateNew(Group, out var id, out _, out _) ? id : throw new InvalidOperationException(),
            "Commodore 64 TOSEC", "c64", "tosec");
        return c;
    }

    private VolumeRecord GroupVolume(CatalogService c, string status = "present")
    {
        var v = new VolumeRecord
        {
            Id = "gv", Label = "c64-tosec-001", PlatformId = "c64", DatLineId = null, GroupId = Group,
            Status = status, Health = "ok", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        };
        c.SaveVolume(v);
        // A current-location row so we can assert DeleteVolume removes it.
        c.SetCurrentLocation(new VolumeLocationRecord
        { Id = Guid.NewGuid().ToString("N"), VolumeId = v.Id, LocationType = "workspace", DiskId = null,
          Path = VolRoot(v.Label), IsCurrent = true, CreatedAt = DateTime.UtcNow });
        Directory.CreateDirectory(VolRoot(v.Label));
        return v;
    }

    // Seeds a leaf DB artifact assigned to the group volume + writes the physical file at the nested source path.
    private (GroupLeafRecord Leaf, string DaId, string RelPath) SeedAssigned(
        CatalogService c, VolumeRecord vol, string leafId, string relId, string fileName, byte[] content)
    {
        var dsp = $"systems/c64/{leafId}.db";
        var abs = Path.Combine(_dataDir, dsp);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var sha1 = Sha1Hex(content);
        var cik  = "sha1:" + sha1 + ":" + leafId + ":" + relId;

        var store = new DatLineStore(abs);
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = leafId, Name = "Game " + relId, Status = "present", ReleaseContentKey = cik });
        store.EnsureContentIdentity(new ContentIdentityRecord
        { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });

        var relPath = $"archive/c64/{leafId}/{fileName}";
        var daId    = store.IngestDerivedArtifact(cik, "", "no_compression", fileName, relPath, content.Length, sha1);
        store.BatchUpdateDerivedArtifactStatus(new[] { daId }, "missing");   // reabsorb should restore it to present

        c.AddVolumeArtifactAndIncrementSize(new VolumeArtifactRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = vol.Id, DatLineId = leafId,
            DerivedArtifactId = daId, ContentIdentityKey = cik, Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
        }, content.Length);

        var segment = GroupRelativeLeafPath.Segment(Group, leafId);
        var dir     = Path.Combine(VolRoot(vol.Label), segment);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), content);

        var leaf = new GroupLeafRecord(
            new DatLineRecord { Id = leafId, HardwareFamilyId = "c64", Name = leafId + " (display)", DataStorePath = dsp },
            new DatLineGroupMetadataRecord { DatLineId = leafId, GroupId = Group });
        return (leaf, daId, relPath);
    }

    private GroupReabsorbPlanningService.Result Plan(CatalogService c, VolumeRecord v, params GroupLeafRecord[] leaves)
        => GroupReabsorbPlanningService.Plan(c, _dataDir, v.Id, Group, VolRoot(v.Label), _appRoot, leaves);

    private static string DaStatus(string dbAbs, string daId)
        => new DatLineStore(dbAbs).GetDerivedArtifacts().Single(d => d.Id == daId).Status;

    // ── Candidate building: leaf-nested source, DB-relative archive dest, exact leaf owner ──

    [Fact]
    public void Plan_BuildsLeafNestedSource_AndDbRelativeDestination()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, daA, relA) = SeedAssigned(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", new byte[]{1,2,3}); // same
        var (b, daB, relB) = SeedAssigned(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", new byte[]{4,5,6,7}); // filename

        var r = Plan(c, v, a, b);

        Assert.Null(r.BlockReason);
        Assert.Equal(2, r.Candidates.Count);
        var ca = r.Candidates.Single(x => x.DerivedArtifactId == daA);
        var cb = r.Candidates.Single(x => x.DerivedArtifactId == daB);

        // Leaf-nested source, distinct per leaf (same filename safe).
        Assert.Equal(Path.Combine(VolRoot(v.Label), "firmware-crt", "game.rom"),     ca.SourcePath);
        Assert.Equal(Path.Combine(VolRoot(v.Label), "games-action-d64", "game.rom"), cb.SourcePath);
        // Archive destination is exactly the DB relative_path (NOT a group-derived archive layout such as
        // archive/<groupId>/…). The leaf directory is the full leaf DatLineId, straight from relative_path.
        Assert.Equal(Path.Combine(_appRoot, relA.Replace('/', Path.DirectorySeparatorChar)), ca.ArchiveDestPath);
        Assert.Equal(Path.Combine(_appRoot, relB.Replace('/', Path.DirectorySeparatorChar)), cb.ArchiveDestPath);
        Assert.Equal("c64-tosec-firmware-crt", ca.ArchiveDestPath.Split(Path.DirectorySeparatorChar)[^2]);
        // Exact leaf ownership; never GroupId.
        Assert.Equal("c64-tosec-firmware-crt",     ca.DatLineId);
        Assert.Equal("c64-tosec-games-action-d64", cb.DatLineId);
    }

    [Fact]
    public void Plan_MissingLeafDb_Blocks()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, _, _) = SeedAssigned(c, v, "c64-tosec-firmware-crt", "r1", "a.rom", new byte[]{1,2,3});
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(_dataDir, a.DatLine.DataStorePath));

        var r = Plan(c, v, a);
        Assert.NotNull(r.BlockReason);
        Assert.Contains("c64-tosec-firmware-crt", r.BlockReason!);
    }

    [Fact] // empty volume → AssignmentCount 0 (caller no-ops); nothing mutated
    public void Plan_EmptyVolume_ZeroAssignments()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var r = Plan(c, v);
        Assert.Equal(0, r.AssignmentCount);
        Assert.Empty(r.Candidates);
        Assert.Null(r.BlockReason);
    }

    // ── Full success across leaves: archive restored, source gone, volume deleted, Group survives ──

    [Fact]
    public void Execute_FullSuccess_MultiLeaf_DeletesVolumeOnly()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, daA, relA) = SeedAssigned(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", new byte[]{1,2,3});
        var (b, daB, relB) = SeedAssigned(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", new byte[]{4,5,6,7});
        var absA = Path.Combine(_dataDir, a.DatLine.DataStorePath);
        var absB = Path.Combine(_dataDir, b.DatLine.DataStorePath);

        var r = Plan(c, v, a, b);
        var res = new VolumeReabsorbService(c).Execute(v.Id, VolRoot(v.Label), r.Candidates);

        Assert.True(res.FullSuccess);
        Assert.True(res.VolumeDeleted);
        Assert.Equal(2, res.RecoveredCount);

        // Archive destinations now contain the files.
        Assert.True(File.Exists(Path.Combine(_appRoot, relA.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(_appRoot, relB.Replace('/', Path.DirectorySeparatorChar))));
        // Volume physical root removed.
        Assert.False(Directory.Exists(VolRoot(v.Label)));
        // Volume record + VA rows + locations removed.
        Assert.Null(c.GetVolumeById(v.Id));
        Assert.Empty(c.GetVolumeArtifacts(v.Id));
        Assert.Null(c.GetCurrentLocation(v.Id));
        // Group + leaf DBs survive; DAs are present in their OWN leaf stores.
        Assert.True(c.DatGroupExists(DatGroupId.FromPersisted(Group)));
        Assert.True(File.Exists(absA));
        Assert.True(File.Exists(absB));
        Assert.Equal("present", DaStatus(absA, daA));
        Assert.Equal("present", DaStatus(absB, daB));
    }

    // ── Partial: a failed (missing-source) artifact aborts; earlier successes committed; volume retained ──

    [Fact]
    public void Execute_Partial_KeepsVolume_RemovesTransferredVAs()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, daA, relA) = SeedAssigned(c, v, "c64-tosec-firmware-crt",     "r1", "a.rom", new byte[]{1,2,3});
        var (b, daB, _)    = SeedAssigned(c, v, "c64-tosec-games-action-d64", "r2", "b.rom", new byte[]{4,5,6,7});

        var r = Plan(c, v, a, b);
        // Deterministic abort: a.rom runs first (recovers), then b.rom's source is missing so its copy throws and
        // the historical catch → abort. Prior success (a) still finalizes; the volume is retained.
        var ordered = r.Candidates.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToList(); // a.rom, b.rom
        File.Delete(ordered[1].SourcePath);

        var res = new VolumeReabsorbService(c).Execute(v.Id, VolRoot(v.Label), ordered);

        Assert.False(res.FullSuccess);
        Assert.False(res.VolumeDeleted);
        Assert.Equal(1, res.RecoveredCount);                       // only a.rom
        Assert.NotNull(c.GetVolumeById(v.Id));                     // volume retained
        // The transferred VA (a) was removed; the failed one (b) remains assigned.
        var vas = c.GetVolumeArtifacts(v.Id);
        Assert.DoesNotContain(vas, x => x.DerivedArtifactId == daA);
        Assert.Contains(vas, x => x.DerivedArtifactId == daB);
        // a's archive copy exists + DA present; a's source removed.
        Assert.True(File.Exists(Path.Combine(_appRoot, relA.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal("present", DaStatus(Path.Combine(_dataDir, a.DatLine.DataStorePath), daA));
    }
}
