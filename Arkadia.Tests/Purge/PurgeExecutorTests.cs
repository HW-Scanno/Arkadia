using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Arkadia.Purge;
using Xunit;

namespace Arkadia.Tests.Purge;

/// <summary>
/// Tests for PurgeReleaseService executor safety semantics.
/// </summary>
public sealed class PurgeExecutorTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _dbPath;
    private readonly string _catalogDbPath;

    public PurgeExecutorTests()
    {
        _tmp           = Path.Combine(Path.GetTempPath(), "ArkPurgeExec_" + Guid.NewGuid().ToString("N")[..8]);
        _dbPath        = Path.Combine(_tmp, "dat", "test.db");
        _catalogDbPath = Path.Combine(_tmp, "catalog.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private DatLineStore   OpenStore()   => new(_dbPath);
    private CatalogService OpenCatalog() => new(_catalogDbPath);

    private string WriteFile(string relPath, byte[]? content = null)
    {
        var full = Path.Combine(_tmp, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content ?? new byte[] { 0xAB, 0xCD });
        return full;
    }

    private string InsertDerived(DatLineStore store, string releaseId,
        string fileName, string relPath, long bytes = 1024)
    {
        var cik = $"sha1:exec{releaseId}{fileName}";
        var id  = store.IngestDerivedArtifact(
            contentIdentityKey: cik, sourceArtifactId: "",
            storageStrategyId: "chd", fileName: fileName,
            relativePath: relPath, derivedSizeBytes: bytes,
            hashedDerivedSha1: "aabbccdd");
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = releaseId,
            ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow,
        });
        return id;
    }

    private PurgeReleasePlan MakePlan(string releaseId, string releaseName, string relPath,
        string fileName, long bytes, string absFilePath, bool fileExists,
        string dbPath, string datLineId = "dl1")
    {
        var la = new PurgeLocalArtifact(
            DerivedArtifactId: "da1",
            FileName:          fileName,
            AbsolutePath:      absFilePath,
            Bytes:             bytes,
            FileExists:        fileExists);

        return new PurgeReleasePlan
        {
            ReleaseId      = releaseId,
            ReleaseName    = releaseName,
            CurrentStatus  = "present",
            DatLineId      = datLineId,
            DbPath         = dbPath,
            LocalArtifacts = [la],
            VolumeArtifacts = [],
            TotalLocalBytes  = bytes,
            TotalVolumeBytes = 0,
            RequiredDiskLabels = [],
            OfflineDiskLabels  = [],
            Warnings = [],
            Issues   = [],
            CanExecute = true,
        };
    }

    // ── Test 10: Purge_DoesNotMarkUnwantedIfDeleteFails ───────────────────────

    [Fact]
    public void Purge_DoesNotMarkUnwantedIfDeleteFails()
    {
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "r1", DatLineId = "dl1", Name = "Game F", Status = "present" }
        });
        InsertDerived(store, "r1", "Game F.chd", "archive/snes/dl1/Game F/Game F.chd");

        // Create a read-only directory to cause a delete failure
        var badPath = Path.Combine(_tmp, "locked", "Game F.chd");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        File.WriteAllBytes(badPath, new byte[] { 1, 2, 3 });

        // Use a non-existent path that will fail
        var plan = MakePlan("r1", "Game F", "archive/snes/dl1/Game F/Game F.chd",
            "Game F.chd", 1024, badPath + "_MISSING_DIR/X", true, _dbPath);

        var catalog = OpenCatalog();
        var svc     = new PurgeReleaseService(_tmp, catalog);
        var result  = svc.Execute(plan);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);

        // Status must remain unchanged
        var releases = store.LoadReleases();
        var r = releases.Find(x => x.Id == "r1");
        Assert.NotNull(r);
        Assert.NotEqual("unwanted", r!.Status);
    }

    // ── Test 11: Purge_MarksUnwantedAfterAllDeletesConfirmed ─────────────────

    [Fact]
    public void Purge_MarksUnwantedAfterAllDeletesConfirmed()
    {
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "r2", DatLineId = "dl1", Name = "Game G", Status = "present" }
        });
        var relPath = "archive/snes/dl1/Game G/Game G.chd";
        InsertDerived(store, "r2", "Game G.chd", relPath, bytes: 2048);
        var absPath = WriteFile(relPath);

        var plan = MakePlan("r2", "Game G", relPath, "Game G.chd", 2048, absPath, true, _dbPath);

        var catalog = OpenCatalog();
        var svc     = new PurgeReleaseService(_tmp, catalog);
        var result  = svc.Execute(plan);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(2048, result.LocalBytesFreed);
        Assert.False(File.Exists(absPath));

        var releases = store.LoadReleases();
        var r = releases.Find(x => x.Id == "r2");
        Assert.NotNull(r);
        Assert.Equal("unwanted", r!.Status);
    }

    // ── Test 12: Purge_RemovesVolumeArtifactRecord ────────────────────────────

    [Fact]
    public void Purge_RemovesOrUpdatesVolumeArtifactRecord()
    {
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "r3", DatLineId = "dl1", Name = "Game H", Status = "present" }
        });

        var relPath = "archive/snes/dl1/Game H/Game H.chd";
        var daId    = InsertDerived(store, "r3", "Game H.chd", relPath, bytes: 512);
        var cik     = store.GetDerivedArtifactsByReleaseId("r3")[0].ContentIdentityKey;
        var absPath = WriteFile(relPath);

        var catalog = OpenCatalog();
        catalog.SaveVolume(new VolumeRecord
        {
            Id = "vol4", Label = "ARKADIA-TEST-0004", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 512,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        var vaId = Guid.NewGuid().ToString("N");
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = vaId, VolumeId = "vol4", DatLineId = "dl1",
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            }
        });

        // Write fake volume file
        var volFile = Path.Combine(_tmp, "volumes", "ARKADIA-TEST-0004", "Game H.chd");
        Directory.CreateDirectory(Path.GetDirectoryName(volFile)!);
        File.WriteAllBytes(volFile, new byte[] { 0x01 });

        // Build plan manually with volume artifact
        var la = new PurgeLocalArtifact("da-local", "Game H.chd", absPath, 512, true);
        var va = new PurgeVolumeArtifact(
            VolumeArtifactId: vaId,
            VolumeId:         "vol4",
            VolumeLabel:      "ARKADIA-TEST-0004",
            DerivedArtifactId: daId,
            DatLineId:        "dl1",
            FileName:         "Game H.chd",
            AbsolutePath:     volFile,
            DiskId:           "",
            DiskLabel:        "—",
            DiskMounted:      true,
            Bytes:            512);

        var plan = new PurgeReleasePlan
        {
            ReleaseId = "r3", ReleaseName = "Game H", CurrentStatus = "present",
            DatLineId = "dl1", DbPath = _dbPath,
            LocalArtifacts  = [la],
            VolumeArtifacts = [va],
            TotalLocalBytes  = 512,
            TotalVolumeBytes = 512,
            RequiredDiskLabels = [], OfflineDiskLabels = [],
            Warnings = [], Issues = [], CanExecute = true,
        };

        var svc    = new PurgeReleaseService(_tmp, catalog);
        var result = svc.Execute(plan);

        Assert.True(result.Success);

        // Volume artifact row should be gone
        var remaining = catalog.GetVolumeArtifacts("vol4");
        Assert.Empty(remaining);
    }

    // ── Test 13: Purge_RefreshesVolumeUsedBytesAfterDelete ───────────────────

    [Fact]
    public void Purge_RefreshesVolumeUsedBytesAfterDelete()
    {
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "r4", DatLineId = "dl1", Name = "Game I", Status = "present" }
        });

        var relPath = "archive/snes/dl1/Game I/Game I.chd";
        var daId    = InsertDerived(store, "r4", "Game I.chd", relPath, bytes: 1000);
        var cik     = store.GetDerivedArtifactsByReleaseId("r4")[0].ContentIdentityKey;
        var absPath = WriteFile(relPath);

        var catalog = OpenCatalog();
        catalog.SaveVolume(new VolumeRecord
        {
            Id = "vol5", Label = "ARKADIA-TEST-0005", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 1000,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        var vaId = Guid.NewGuid().ToString("N");
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = vaId, VolumeId = "vol5", DatLineId = "dl1",
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            }
        });

        var volFile = Path.Combine(_tmp, "volumes", "ARKADIA-TEST-0005", "Game I.chd");
        Directory.CreateDirectory(Path.GetDirectoryName(volFile)!);
        File.WriteAllBytes(volFile, new byte[] { 0x01 });

        var la = new PurgeLocalArtifact("da-l", "Game I.chd", absPath, 1000, true);
        var va = new PurgeVolumeArtifact(vaId, "vol5", "ARKADIA-TEST-0005", daId, "dl1",
            "Game I.chd", volFile, "", "—", true, 1000);

        var plan = new PurgeReleasePlan
        {
            ReleaseId = "r4", ReleaseName = "Game I", CurrentStatus = "present",
            DatLineId = "dl1", DbPath = _dbPath,
            LocalArtifacts  = [la],
            VolumeArtifacts = [va],
            TotalLocalBytes = 1000, TotalVolumeBytes = 1000,
            RequiredDiskLabels = [], OfflineDiskLabels = [],
            Warnings = [], Issues = [], CanExecute = true,
        };

        var volBefore = catalog.GetVolumeById("vol5")!;
        Assert.Equal(1000, volBefore.ActualSizeBytes);

        var svc    = new PurgeReleaseService(_tmp, catalog);
        var result = svc.Execute(plan);

        Assert.True(result.Success);
        var volAfter = catalog.GetVolumeById("vol5")!;
        Assert.Equal(0, volAfter.ActualSizeBytes);  // decremented by 1000
    }

    // ── Test 14: Purge_DoesNotDeleteUnplannedFiles ────────────────────────────

    [Fact]
    public void Purge_DoesNotDeleteUnplannedFiles()
    {
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "r5", DatLineId = "dl1", Name = "Game J", Status = "present" }
        });

        var relPath = "archive/snes/dl1/Game J/Game J.chd";
        InsertDerived(store, "r5", "Game J.chd", relPath, bytes: 256);
        var absPath = WriteFile(relPath);

        // An unrelated file in the same dir
        var otherFile = Path.Combine(_tmp, "archive", "snes", "dl1", "Game J", "other.txt");
        File.WriteAllText(otherFile, "keep me");

        var plan = MakePlan("r5", "Game J", relPath, "Game J.chd", 256, absPath, true, _dbPath);
        var svc  = new PurgeReleaseService(_tmp, OpenCatalog());
        var res  = svc.Execute(plan);

        Assert.True(res.Success);
        Assert.False(File.Exists(absPath));
        Assert.True(File.Exists(otherFile), "Unplanned file must not be deleted");
    }

    // ── Test 15: Purge_StopsIfFileAlreadyAbsent ───────────────────────────────

    [Fact]
    public void Purge_StopsIfFileAlreadyAbsent_ReportsSuccess()
    {
        // If the file is already gone (FileExists = false), purge should still
        // proceed and mark release unwanted (treat absent as already purged).
        var store = OpenStore();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "r6", DatLineId = "dl1", Name = "Game K", Status = "present" }
        });

        var relPath = "archive/snes/dl1/Game K/Game K.chd";
        InsertDerived(store, "r6", "Game K.chd", relPath);
        var absPath = Path.Combine(_tmp, relPath.Replace('/', Path.DirectorySeparatorChar));
        // File NOT written — FileExists = false

        var plan = MakePlan("r6", "Game K", relPath, "Game K.chd", 1024, absPath, false, _dbPath);
        var svc  = new PurgeReleaseService(_tmp, OpenCatalog());
        var res  = svc.Execute(plan);

        Assert.True(res.Success);
        Assert.Equal(0, res.FilesDeleted);  // nothing to delete, but still marks unwanted

        var releases = store.LoadReleases();
        var r = releases.Find(x => x.Id == "r6");
        Assert.Equal("unwanted", r!.Status);
    }
}
