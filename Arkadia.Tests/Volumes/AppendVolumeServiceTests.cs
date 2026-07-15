using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Arkadia.Data;
using Arkadia.LocalArchive;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Tests for AppendVolumeService — execution of the Append Volume plan.
/// </summary>
public sealed class AppendVolumeServiceTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _catalogDbPath;
    private readonly string _datDbPath;

    public AppendVolumeServiceTests()
    {
        _tmp           = Path.Combine(Path.GetTempPath(), "ArkAppSvc_" + Guid.NewGuid().ToString("N")[..8]);
        _catalogDbPath = Path.Combine(_tmp, "catalog.db");
        _datDbPath     = Path.Combine(_tmp, "dat.db");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CatalogService OpenCatalog() => new(_catalogDbPath);
    private DatLineStore   OpenStore()   => new(_datDbPath);

    private static string Sha1Hex(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private string VolumeRoot(string label)
        => Path.Combine(_tmp, "volumes", label);

    private string ArchivePath(string fileName, string datLineId = "dl1")
        => Path.Combine(_tmp, "archive", "snes", datLineId, fileName);

    private void WriteArchiveFile(string fileName, byte[] content, string datLineId = "dl1")
    {
        var dir = Path.Combine(_tmp, "archive", "snes", datLineId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), content);
    }

    private static string RelPath(string fileName, string datLineId = "dl1")
        => $"archive/snes/{datLineId}/{fileName}";

    /// <summary>
    /// Provisions one artifact in both stores and writes the archive file.
    /// Returns (VolumeRecord, daId, contentIdentityKey).
    /// </summary>
    private (VolumeRecord Vol, string DaId, string Cik) ProvisionArtifact(
        string volLabel, string fileName, byte[] content,
        string datLineId = "dl1", long plannedSizeBytes = 10_000_000)
    {
        var catalog = OpenCatalog();
        var store   = OpenStore();

        var sha1  = Sha1Hex(content);
        var cik   = $"sha1:{sha1}";
        var relId = Guid.NewGuid().ToString("N");
        var volId = Guid.NewGuid().ToString("N");

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = datLineId, Name = "Test Release", Status = "present"
        });
        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null,
            CreatedAtUtc = DateTime.UtcNow
        });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId,
            ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow
        });
        var daId = store.IngestDerivedArtifact(
            cik, "", "chd", fileName, RelPath(fileName, datLineId), content.Length, sha1);

        var vol = new VolumeRecord
        {
            Id = volId, Label = volLabel, PlatformId = "snes", DatLineId = datLineId,
            Status = "present", PlannedSizeBytes = plannedSizeBytes, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        };
        catalog.SaveVolume(vol);
        WriteArchiveFile(fileName, content, datLineId);

        return (vol, daId, cik);
    }

    private AppendVolumePlan BuildPlan(VolumeRecord volume,
        string? volRoot = null, string? archiveRoot = null)
    {
        volRoot     ??= VolumeRoot(volume.Label);
        archiveRoot ??= _tmp;
        Directory.CreateDirectory(volRoot);
        return new AppendVolumePlanner(OpenCatalog())
            .Plan(volume, volRoot, archiveRoot, OpenStore());
    }

    // ── 12. CopiesVerifiesThenCreatesVolumeArtifactRow ────────────────────────

    [Fact]
    public void AppendExecution_CopiesVerifiesThenCreatesVolumeArtifactRow()
    {
        var content = new byte[] { 10, 20, 30, 40, 50 };
        var (vol, daId, _) = ProvisionArtifact("vol-svc-src", "Game.chd", content);
        var volRoot = VolumeRoot(vol.Label);

        var plan   = BuildPlan(vol, volRoot);
        Assert.True(plan.CanExecute, "plan must be executable");

        var catalog = OpenCatalog();
        var svc     = new AppendVolumeService(catalog);
        var result  = svc.Execute(plan);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(1, result.CopiedCount);
        Assert.Equal(content.Length, result.BytesCopied);

        // Physical file must exist on volume
        Assert.True(File.Exists(Path.Combine(volRoot, "Game.chd")));

        // VA row must exist in catalog
        Assert.True(catalog.VolumeArtifactExists(vol.Id, daId));
    }

    // ── 13. CopyVerifyCommitDeletesArchiveSource ──────────────────────────────

    [Fact]
    public void AppendExecution_CopyVerifyCommitDeletesArchiveSource()
    {
        var content    = new byte[] { 1, 2, 3 };
        var (vol, _, _) = ProvisionArtifact("vol-delete", "Archive.chd", content);
        var volRoot    = VolumeRoot(vol.Label);
        var archiveSrc = ArchivePath("Archive.chd");

        Assert.True(File.Exists(archiveSrc), "archive file must exist before execution");

        var plan   = BuildPlan(vol, volRoot);
        var result = new AppendVolumeService(OpenCatalog()).Execute(plan);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(1, result.CopiedCount);
        Assert.Equal(1, result.SourcesDeletedCount);
        Assert.Equal(0, result.SourceDeleteFailedCount);
        Assert.False(File.Exists(archiveSrc), "archive source must be deleted after successful append");
        Assert.True(File.Exists(Path.Combine(volRoot, "Archive.chd")), "volume copy must exist");
    }

    // ── 16. DoesNotDeleteArchiveSourceWhenCopyFails ───────────────────────────

    [Fact]
    public void AppendExecution_DoesNotDeleteArchiveSourceWhenCopyFails()
    {
        var content    = new byte[] { 1, 2, 3 };
        var (vol, _, _) = ProvisionArtifact("vol-copyfail", "CopyFail.chd", content);
        var volRoot    = VolumeRoot(vol.Label);
        var archiveSrc = ArchivePath("CopyFail.chd");

        var plan = BuildPlan(vol, volRoot);
        Assert.True(plan.CanExecute, "plan must be executable");

        // Remove volume root so File.Copy fails with DirectoryNotFoundException
        Directory.Delete(volRoot, recursive: false);

        var result = new AppendVolumeService(OpenCatalog()).Execute(plan);

        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(0, result.CopiedCount);
        Assert.Equal(0, result.SourcesDeletedCount);
        Assert.True(File.Exists(archiveSrc), "archive source must not be deleted when copy fails");
    }

    // ── 17. DoesNotDeleteArchiveSourceIfVerifyFails ───────────────────────────

    [Fact]
    public void AppendExecution_DoesNotDeleteArchiveSourceIfVerifyFails()
    {
        var content    = new byte[] { 10, 20, 30 };
        var (vol, _, _) = ProvisionArtifact("vol-verifyfail", "VerifyFail.chd", content);
        var volRoot    = VolumeRoot(vol.Label);
        var archiveSrc = ArchivePath("VerifyFail.chd");

        // Tamper with the archive file so its SHA1 no longer matches the DB
        File.WriteAllBytes(archiveSrc, new byte[] { 0xFF });

        // The planner still plans it (hash in DB is non-null); size mismatch triggers verify fail
        var plan = BuildPlan(vol, volRoot);
        Assert.True(plan.CanExecute, "plan must be executable");

        var result = new AppendVolumeService(OpenCatalog()).Execute(plan);

        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(0, result.CopiedCount);
        Assert.Equal(0, result.SourcesDeletedCount);
        Assert.True(File.Exists(archiveSrc), "archive source must not be deleted when verify fails");
    }

    // ── 18. OnlyDeletesSourceOfSuccessfulEntries ─────────────────────────────

    [Fact]
    public void AppendExecution_OnlyDeletesSourceOfSuccessfulEntries()
    {
        // Plan with two entries: A succeeds fully, B's copy fails because the
        // target slot is already occupied. Source of A must be deleted; B's must not.
        var contentA = new byte[] { 1, 2, 3 };
        var contentB = new byte[] { 4, 5, 6 };

        var (vol, _, _) = ProvisionArtifact("vol-multi", "ArtA.chd", contentA);
        var volRoot = VolumeRoot(vol.Label);
        var srcA    = ArchivePath("ArtA.chd");

        // Provision artifact B into the same DatLineStore/DatLine
        var sha1B  = Sha1Hex(contentB);
        var cikB   = $"sha1:{sha1B}";
        var relIdB = Guid.NewGuid().ToString("N");
        var store  = OpenStore();
        store.UpsertRelease(new ReleaseRecord
            { Id = relIdB, DatLineId = "dl1", Name = "Release B", Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord
            { ContentIdentityKey = cikB, DatSha1 = sha1B, DatMd5 = null,
              DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
            { Id = Guid.NewGuid().ToString("N"), ReleaseId = relIdB,
              ContentIdentityKey = cikB, CreatedAtUtc = DateTime.UtcNow });
        store.IngestDerivedArtifact(cikB, "", "chd", "ArtB.chd",
            "archive/snes/dl1/ArtB.chd", contentB.Length, sha1B);
        WriteArchiveFile("ArtB.chd", contentB);
        var srcB = ArchivePath("ArtB.chd");

        var plan = BuildPlan(vol, volRoot);
        Assert.Equal(2, plan.PlannedCount);

        // Pre-create ArtB.chd in the volume root so File.Copy (overwrite:false) fails for B
        File.WriteAllBytes(Path.Combine(volRoot, "ArtB.chd"), new byte[] { 0xFF });

        var result = new AppendVolumeService(OpenCatalog()).Execute(plan);

        Assert.Equal(1, result.CopiedCount);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(1, result.SourcesDeletedCount);
        Assert.Equal(0, result.SourceDeleteFailedCount);
        Assert.False(File.Exists(srcA), "A's source must be deleted after successful transfer");
        Assert.True(File.Exists(srcB), "B's source must be preserved because copy failed");
    }

    // ── 19. LeavesVolumeAssignmentIfSourceDeleteFails ─────────────────────────

    [Fact]
    public void AppendExecution_LeavesVolumeAssignmentIfSourceDeleteFails()
    {
        var content         = new byte[] { 7, 8, 9 };
        var (vol, daId, _)  = ProvisionArtifact("vol-delfail", "DelFail.chd", content);
        var volRoot         = VolumeRoot(vol.Label);
        var archiveSrc      = ArchivePath("DelFail.chd");

        var plan = BuildPlan(vol, volRoot);
        Assert.True(plan.CanExecute, "plan must be executable");

        // Make archive source read-only so File.Delete throws UnauthorizedAccessException
        File.SetAttributes(archiveSrc, FileAttributes.ReadOnly);
        try
        {
            var catalog = OpenCatalog();
            var result  = new AppendVolumeService(catalog).Execute(plan);

            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(1, result.CopiedCount);
            Assert.Equal(0, result.SourcesDeletedCount);
            Assert.Equal(1, result.SourceDeleteFailedCount);

            // VA row must still exist despite source delete failure
            Assert.True(catalog.VolumeArtifactExists(vol.Id, daId));

            // Volume copy must exist
            Assert.True(File.Exists(Path.Combine(volRoot, "DelFail.chd")));
        }
        finally
        {
            File.SetAttributes(archiveSrc, FileAttributes.Normal);
        }
    }

    // ── 20. ReportsSourceDeleteFailure ───────────────────────────────────────

    [Fact]
    public void AppendExecution_ReportsSourceDeleteFailure()
    {
        var content    = new byte[] { 11, 22, 33 };
        var (vol, _, _) = ProvisionArtifact("vol-delfail2", "DelFail2.chd", content);
        var volRoot    = VolumeRoot(vol.Label);
        var archiveSrc = ArchivePath("DelFail2.chd");

        var plan = BuildPlan(vol, volRoot);
        Assert.True(plan.CanExecute, "plan must be executable");

        File.SetAttributes(archiveSrc, FileAttributes.ReadOnly);
        try
        {
            var captured = new List<AppendVolumeProgress>();
            var result   = new AppendVolumeService(OpenCatalog()).Execute(
                plan,
                new SyncProgress<AppendVolumeProgress>(captured.Add));

            Assert.Equal(1, result.SourceDeleteFailedCount);
            Assert.Contains(captured, p =>
                p.Action   == "append-source-delete-failed" &&
                p.FileName == "DelFail2.chd");
        }
        finally
        {
            File.SetAttributes(archiveSrc, FileAttributes.Normal);
        }
    }

    // ── 21. DeletesOnlyAfterVolumeArtifactRowCreated ──────────────────────────

    [Fact]
    public void AppendExecution_DeletesOnlyAfterVolumeArtifactRowCreated()
    {
        var content        = new byte[] { 1, 2, 3, 4, 5 };
        var (vol, daId, _)  = ProvisionArtifact("vol-order", "Order.chd", content);
        var volRoot        = VolumeRoot(vol.Label);
        var archiveSrc     = ArchivePath("Order.chd");

        var plan    = BuildPlan(vol, volRoot);
        var catalog = OpenCatalog();
        var result  = new AppendVolumeService(catalog).Execute(plan);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(1, result.CopiedCount);

        // VA row must exist after successful run
        Assert.True(catalog.VolumeArtifactExists(vol.Id, daId), "VA row must exist");

        // Archive source must be gone (deleted after VA row committed)
        Assert.False(File.Exists(archiveSrc), "source must be deleted after VA row is created");
    }

    // ── 22. DerivedArtifactRowRemainsAfterSourceDelete ────────────────────────

    [Fact]
    public void AppendExecution_DerivedArtifactRowRemainsAfterSourceDelete()
    {
        var content        = new byte[] { 99, 88 };
        var (vol, daId, _)  = ProvisionArtifact("vol-darow", "DaRow.chd", content);
        var volRoot        = VolumeRoot(vol.Label);
        var archiveSrc     = ArchivePath("DaRow.chd");

        var plan   = BuildPlan(vol, volRoot);
        var result = new AppendVolumeService(OpenCatalog()).Execute(plan);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(1, result.SourcesDeletedCount);
        Assert.False(File.Exists(archiveSrc), "source must be deleted");

        // DA row in DatLineStore must still exist — only the archive file was deleted
        var artifacts = OpenStore().GetAllArchiveArtifactInfos();
        Assert.Contains(artifacts, a => a.DerivedArtifactId == daId);
    }

    // ── 23. AppendPlan_DoesNotPlanAlreadyAssignedArtifactAfterSourceDelete ────

    [Fact]
    public void AppendPlan_DoesNotPlanAlreadyAssignedArtifactAfterSourceDelete()
    {
        var content        = new byte[] { 55, 66, 77 };
        var (vol, _, _)    = ProvisionArtifact("vol-replan", "Replan.chd", content);
        var volRoot        = VolumeRoot(vol.Label);
        var archiveSrc     = ArchivePath("Replan.chd");

        // First Append — source is deleted, VA row created
        var plan1   = BuildPlan(vol, volRoot);
        var result1 = new AppendVolumeService(OpenCatalog()).Execute(plan1);
        Assert.Equal(1, result1.CopiedCount);
        Assert.Equal(1, result1.SourcesDeletedCount);
        Assert.False(File.Exists(archiveSrc));

        // Re-plan on the same volume — artifact is now assigned, must be skipped
        var plan2 = BuildPlan(vol, volRoot);
        Assert.Equal(0, plan2.PlannedCount);
        Assert.Equal(1, plan2.AlreadyAssignedSkipped);
    }

    // ── 24. VerifyArchive_DoesNotReportDeletedAssignedArchiveSourceAsMissing ──

    [Fact]
    public void VerifyArchive_DoesNotReportDeletedAssignedArchiveSourceAsMissing()
    {
        var content    = new byte[] { 3, 6, 9 };
        var (vol, _, _) = ProvisionArtifact("vol-absent", "Absent.chd", content);
        var volRoot    = VolumeRoot(vol.Label);
        var archiveSrc = ArchivePath("Absent.chd");

        // Run Append — source deleted
        var plan   = BuildPlan(vol, volRoot);
        var result = new AppendVolumeService(OpenCatalog()).Execute(plan);
        Assert.Equal(1, result.CopiedCount);
        Assert.Equal(1, result.SourcesDeletedCount);
        Assert.False(File.Exists(archiveSrc), "source must be deleted by Append");

        // Verify Archive — the absent source must not surface as a scan entry;
        // it must only appear in AbsentFromArchiveCount
        var verifyPlan = new LocalArchiveVerifyService(_tmp).Verify("snes", "dl1", OpenStore());
        Assert.Empty(verifyPlan.Entries);
        Assert.Equal(1, verifyPlan.AbsentFromArchiveCount);
    }

    // ── 25. VerifyArchive_RedundantCopyOnlyWhenArchiveFileStillExists ─────────

    [Fact]
    public void VerifyArchive_RedundantCopyOnlyWhenArchiveFileStillExists()
    {
        var content    = new byte[] { 2, 4, 8 };
        var (vol, daId, _) = ProvisionArtifact("vol-redund", "Redund.chd", content);
        var volRoot    = VolumeRoot(vol.Label);
        var archiveSrc = ArchivePath("Redund.chd");

        Directory.CreateDirectory(volRoot);

        // Build assignment dict: artifact is on a reachable volume
        var av = new Dictionary<string, AssignedVolumeInfo>
        {
            [daId] = new AssignedVolumeInfo(vol.Id, vol.Label, volRoot),
        };

        // Scenario A: archive file still present → RedundantArchiveCopy
        Assert.True(File.Exists(archiveSrc));
        var svc   = new LocalArchiveVerifyService(_tmp);
        var planA = svc.Verify("snes", "dl1", OpenStore(), assignedVolumes: av);
        Assert.Equal(1, planA.RedundantCopies);
        Assert.Equal(0, planA.AbsentFromArchiveCount);

        // Scenario B: archive source deleted (as Append would do) → no RedundantArchiveCopy
        File.Delete(archiveSrc);
        var planB = svc.Verify("snes", "dl1", OpenStore(), assignedVolumes: av);
        Assert.Equal(0, planB.RedundantCopies);
        Assert.Equal(1, planB.AbsentFromArchiveCount);
    }

    // ── 14. RefreshesVolumeUsageAfterSuccess ──────────────────────────────────

    [Fact]
    public void AppendExecution_RefreshesVolumeUsageAfterSuccess()
    {
        var content = new byte[] { 7, 8, 9, 10 };
        var (vol, _, _) = ProvisionArtifact("vol-usage", "Usage.chd", content);
        var volRoot = VolumeRoot(vol.Label);

        var catalog = OpenCatalog();
        var before  = catalog.GetVolumeById(vol.Id)!;
        Assert.Equal(0, before.ActualSizeBytes);

        var plan   = BuildPlan(vol, volRoot);
        new AppendVolumeService(catalog).Execute(plan);

        var after = catalog.GetVolumeById(vol.Id)!;
        Assert.Equal(content.Length, after.ActualSizeBytes);
    }

    // ── 15. DoesNotCreateVolumeArtifactForUnwanted ───────────────────────────

    [Fact]
    public void AppendExecution_DoesNotCreateVolumeArtifactForUnwanted()
    {
        // An unwanted artifact is excluded at the planner level; the service must
        // not create any VA row even if given an empty-but-valid plan.
        var store   = OpenStore();
        var catalog = OpenCatalog();
        var content = new byte[] { 11, 22, 33 };
        var sha1    = Sha1Hex(content);
        var cik     = $"sha1:{sha1}";
        var relId   = Guid.NewGuid().ToString("N");
        var volId   = Guid.NewGuid().ToString("N");

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = "dl1", Name = "Unwanted Game", Status = "unwanted"
        });
        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = cik, DatSha1 = sha1,
            DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow
        });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId,
            ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow
        });
        store.IngestDerivedArtifact(cik, "", "chd", "Unwanted.chd",
            RelPath("Unwanted.chd"), content.Length, sha1);
        WriteArchiveFile("Unwanted.chd", content);

        var vol = new VolumeRecord
        {
            Id = volId, Label = "vol-unwanted-svc", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 10_000_000, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        };
        catalog.SaveVolume(vol);

        var plan = BuildPlan(vol);

        // Plan must have 0 planned (unwanted filtered at DB level)
        Assert.Equal(0, plan.PlannedCount);
        Assert.False(plan.CanExecute);

        // Executing the plan must create 0 VA rows
        var svc    = new AppendVolumeService(catalog);
        var result = svc.Execute(plan);

        Assert.Equal(0, result.CopiedCount);
        var vas = catalog.GetVolumeArtifacts(vol.Id);
        Assert.Empty(vas);
    }
}

// ── Test helper: synchronous IProgress<T> ────────────────────────────────────

internal sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
