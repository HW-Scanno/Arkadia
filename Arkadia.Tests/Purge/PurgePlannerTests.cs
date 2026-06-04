using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Arkadia.Purge;
using Xunit;

namespace Arkadia.Tests.Purge;

/// <summary>
/// Tests for PurgeReleasePlanner.
/// All tests use temp dir DBs and real filesystem paths.
/// </summary>
public sealed class PurgePlannerTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _dbPath;
    private readonly string _catalogDbPath;

    public PurgePlannerTests()
    {
        _tmp           = Path.Combine(Path.GetTempPath(), "ArkPurgePlan_" + Guid.NewGuid().ToString("N")[..8]);
        _dbPath        = Path.Combine(_tmp, "dat", "test.db");
        _catalogDbPath = Path.Combine(_tmp, "catalog.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private DatLineStore  OpenStore()   => new(_dbPath);
    private CatalogService OpenCatalog() => new(_catalogDbPath);

    private string WriteFakeFile(string relPath)
    {
        var full = Path.Combine(_tmp, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[] { 0xAA, 0xBB });
        return full;
    }

    // ── Helper: insert a derived artifact + release_content_link ─────────────

    private string InsertDerived(DatLineStore store,
        string releaseId, string fileName, string relPath, long bytes = 1024)
    {
        var cik = $"sha1:test{releaseId}{fileName.GetHashCode():X}";
        var id  = store.IngestDerivedArtifact(
            contentIdentityKey: cik,
            sourceArtifactId:   "",
            storageStrategyId:  "chd",
            fileName:           fileName,
            relativePath:       relPath,
            derivedSizeBytes:   bytes,
            hashedDerivedSha1:  "deadbeef00001111");
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id                 = Guid.NewGuid().ToString("N"),
            ReleaseId          = releaseId,
            ContentIdentityKey = cik,
            CreatedAtUtc       = DateTime.UtcNow,
        });
        return id;
    }

    // ── Test 5: PurgePlan_FindsLocalArchiveArtifact ───────────────────────────

    [Fact]
    public void PurgePlan_FindsLocalArchiveArtifact()
    {
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "rel1", DatLineId = "dl1", Name = "Game A", Status = "present" }
        });

        var relPath = "archive/snes/dl1/Game A/Game A.chd";
        InsertDerived(store, "rel1", "Game A.chd", relPath, bytes: 4096);
        WriteFakeFile(relPath);

        var catalog = OpenCatalog();
        var planner = new PurgeReleasePlanner(_tmp, catalog);
        var plan    = planner.Plan("rel1", "Game A", "present", "dl1", _dbPath);

        var la = Assert.Single(plan.LocalArtifacts);
        Assert.True(la.FileExists);
        Assert.Equal("Game A.chd", la.FileName);
        Assert.Equal(4096, plan.TotalLocalBytes);
    }

    // ── Test 6: PurgePlan_FindsVolumeArtifact_Workspace ──────────────────────

    [Fact]
    public void PurgePlan_FindsVolumeArtifact_Workspace()
    {
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "rel2", DatLineId = "dl1", Name = "Game B", Status = "present" }
        });

        var relPath = "archive/snes/dl1/Game B/Game B.chd";
        var daId    = InsertDerived(store, "rel2", "Game B.chd", relPath, bytes: 2048);
        WriteFakeFile(relPath);

        var catalog = OpenCatalog();
        var cik     = store.GetDerivedArtifactsByReleaseId("rel2")[0].ContentIdentityKey;

        catalog.SaveVolume(new VolumeRecord
        {
            Id = "vol1", Label = "ARKADIA-SNES-0001", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 2048,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        catalog.SetCurrentLocation(new VolumeLocationRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = "vol1",
            LocationType = "workspace", IsCurrent = true, CreatedAt = DateTime.UtcNow,
        });
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = "vol1", DatLineId = "dl1",
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            }
        });

        // Write fake volume file
        var volDir = Path.Combine(_tmp, "volumes", "ARKADIA-SNES-0001");
        Directory.CreateDirectory(volDir);
        File.WriteAllBytes(Path.Combine(volDir, "Game B.chd"), new byte[] { 0xCC });

        var planner = new PurgeReleasePlanner(_tmp, catalog);
        var plan    = planner.Plan("rel2", "Game B", "present", "dl1", _dbPath);

        var va = Assert.Single(plan.VolumeArtifacts);
        Assert.NotNull(va.AbsolutePath);
        Assert.Equal("ARKADIA-SNES-0001", va.VolumeLabel);
    }

    // ── Test 7: PurgePlan_RequiresOfflineDisk ────────────────────────────────

    [Fact]
    public void PurgePlan_RequiresOfflineDisk_AddsToDiskLabels()
    {
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "rel3", DatLineId = "dl1", Name = "Game C", Status = "present" }
        });

        var daId = InsertDerived(store, "rel3", "Game C.chd", "archive/snes/dl1/Game C/Game C.chd");
        var cik  = store.GetDerivedArtifactsByReleaseId("rel3")[0].ContentIdentityKey;

        var catalog = OpenCatalog();
        catalog.SaveDisk(new DiskRecord
        {
            Id = "disk1", Label = "ARKADIA-0001", Status = "assigned",
            DeclaredCapacityBytes = 1_000_000, Filesystem = "exFAT",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Family = "core",
        });
        catalog.SaveVolume(new VolumeRecord
        {
            Id = "vol2", Label = "ARKADIA-SNES-0002", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 512,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        catalog.SetCurrentLocation(new VolumeLocationRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = "vol2",
            LocationType = "disk", DiskId = "disk1", IsCurrent = true, CreatedAt = DateTime.UtcNow,
        });
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = "vol2", DatLineId = "dl1",
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            }
        });

        var planner = new PurgeReleasePlanner(_tmp, catalog);
        var plan    = planner.Plan("rel3", "Game C", "present", "dl1", _dbPath);

        Assert.Contains("ARKADIA-0001", plan.RequiredDiskLabels);
    }

    // ── Test 8: PurgePlan_LocalArtifactFileExistsFalse ───────────────────────

    [Fact]
    public void PurgePlan_LocalArtifactFileExistsFalse_WhenMissing()
    {
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "rel4", DatLineId = "dl1", Name = "Game D", Status = "missing" }
        });

        InsertDerived(store, "rel4", "Game D.chd", "archive/snes/dl1/Game D/Game D.chd");
        // Do NOT write the file

        var catalog = OpenCatalog();
        var planner = new PurgeReleasePlanner(_tmp, catalog);
        var plan    = planner.Plan("rel4", "Game D", "missing", "dl1", _dbPath);

        var laD = Assert.Single(plan.LocalArtifacts);
        Assert.False(laD.FileExists);
        Assert.True(plan.CanExecute, "Plan with absent file is still executable");
    }

    // ── Test 9: PurgePlan_BlocksWhenRequiredDiskOffline ──────────────────────

    [Fact]
    public void PurgePlan_BlocksWhenRequiredDiskOffline()
    {
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "rel5", DatLineId = "dl1", Name = "Game E", Status = "present" }
        });

        var daId = InsertDerived(store, "rel5", "Game E.chd", "archive/snes/dl1/Game E/Game E.chd");
        var cik  = store.GetDerivedArtifactsByReleaseId("rel5")[0].ContentIdentityKey;

        var catalog = OpenCatalog();
        catalog.SaveDisk(new DiskRecord
        {
            Id = "disk2", Label = "ARKADIA-0002", Status = "assigned",
            DeclaredCapacityBytes = 1_000_000, Filesystem = "exFAT",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Family = "core",
        });
        catalog.SaveVolume(new VolumeRecord
        {
            Id = "vol3", Label = "ARKADIA-VOL-003", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 256,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        catalog.SetCurrentLocation(new VolumeLocationRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = "vol3",
            LocationType = "disk", DiskId = "disk2", IsCurrent = true, CreatedAt = DateTime.UtcNow,
        });
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = "vol3", DatLineId = "dl1",
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            }
        });

        var planner = new PurgeReleasePlanner(_tmp, catalog);
        var plan    = planner.Plan("rel5", "Game E", "present", "dl1", _dbPath);

        Assert.False(plan.CanExecute, "Plan must be blocked when required disk is offline");
        Assert.NotEmpty(plan.OfflineDiskLabels);
        Assert.NotEmpty(plan.Issues);
    }
}
