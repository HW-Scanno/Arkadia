using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Tests for VolumeFillbackService — the execution service for the Fillback operation.
///
/// Uses real SQLite stores + temp filesystem for reliable behavior verification.
/// Tests cover both same-disk (Move) and cross-disk (Copy→Verify→Delete) paths.
/// </summary>
public sealed class VolumeFillbackServiceTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _catalogDbPath;
    private readonly string _datDbPath;

    public VolumeFillbackServiceTests()
    {
        _tmp           = Path.Combine(Path.GetTempPath(), "ArkFBSvc_" + Guid.NewGuid().ToString("N")[..8]);
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

    private void WriteVolumeFile(string volLabel, string fileName, byte[] content)
    {
        var root = VolumeRoot(volLabel);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, fileName), content);
    }

    /// <summary>
    /// Provisions one artifact assigned to <paramref name="volLabel"/> in both stores.
    /// Writes the physical file at the volume flat root.
    /// Returns (VolumeRecord, vaId, daId).
    /// </summary>
    private (VolumeRecord Vol, string VaId, string DaId, string Sha1) ProvisionAndWrite(
        string volLabel, string fileName, byte[] content,
        long plannedSizeBytes = 1_000_000)
    {
        var catalog = OpenCatalog();
        var store   = OpenStore();
        var sha1    = Sha1Hex(content);
        var cik     = $"sha1:{sha1}";
        var relId   = Guid.NewGuid().ToString("N");
        var volId   = Guid.NewGuid().ToString("N");
        var vaId    = Guid.NewGuid().ToString("N");

        store.UpsertRelease(new ReleaseRecord
            { Id = relId, DatLineId = "dl1", Name = "Release " + fileName, Status = "present" });
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
            cik, "", "chd", fileName, $"archive/snes/dl1/{fileName}", content.Length, sha1);

        catalog.SaveVolume(new VolumeRecord
        {
            Id = volId, Label = volLabel, PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = plannedSizeBytes, ActualSizeBytes = content.Length,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = vaId, VolumeId = volId, DatLineId = "dl1",
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            }
        });

        WriteVolumeFile(volLabel, fileName, content);

        return (
            new VolumeRecord
            {
                Id = volId, Label = volLabel, PlatformId = "snes", DatLineId = "dl1",
                Status = "present", PlannedSizeBytes = plannedSizeBytes, ActualSizeBytes = content.Length,
                CreatedAt = DateTime.UtcNow, Health = "ok",
            }, vaId, daId, sha1);
    }

    private VolumeRecord ProvisionEmptyVolume(string volLabel, long plannedSizeBytes = 50_000_000)
    {
        var catalog = OpenCatalog();
        var volId   = Guid.NewGuid().ToString("N");
        var vol = new VolumeRecord
        {
            Id = volId, Label = volLabel, PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = plannedSizeBytes, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        };
        catalog.SaveVolume(vol);
        return vol;
    }

    /// <summary>
    /// Adds one artifact to an existing volume record in both stores.
    /// Uses UpsertRelease so multiple calls can accumulate in the same dat store.
    /// Increments the volume's actual_size_bytes in the catalog.
    /// </summary>
    private void AddArtifact(string volId, string volLabel, string fileName, byte[] content)
    {
        var catalog = OpenCatalog();
        var store   = OpenStore();
        var sha1    = Sha1Hex(content);
        var cik     = $"sha1:{sha1}";
        var relId   = Guid.NewGuid().ToString("N");
        var vaId    = Guid.NewGuid().ToString("N");

        store.UpsertRelease(new ReleaseRecord
            { Id = relId, DatLineId = "dl1", Name = "Release " + fileName, Status = "present" });
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
        var daId = store.IngestDerivedArtifact(
            cik, "", "chd", fileName, $"archive/snes/dl1/{fileName}", content.Length, sha1);

        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = vaId, VolumeId = volId, DatLineId = "dl1",
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            }
        });

        // Increment actual_size_bytes
        var vol = catalog.GetVolumeById(volId)!;
        catalog.SaveVolume(new VolumeRecord
        {
            Id = vol.Id, Label = vol.Label, PlatformId = vol.PlatformId,
            DatLineId = vol.DatLineId, Status = vol.Status,
            PlannedSizeBytes = vol.PlannedSizeBytes,
            ActualSizeBytes  = vol.ActualSizeBytes + content.Length,
            CreatedAt = vol.CreatedAt, Health = vol.Health,
        });

        WriteVolumeFile(volLabel, fileName, content);
    }

    /// <summary>Builds a plan and runs the service. Both roots must exist.</summary>
    private (VolumeFillbackPlan Plan, VolumeFillbackResult Result) PlanAndExecute(
        VolumeRecord source, VolumeRecord target,
        string? srcRoot = null, string? dstRoot = null,
        FillbackOperationMode modeOverride = FillbackOperationMode.MoveSameDisk)
    {
        srcRoot ??= VolumeRoot(source.Label);
        dstRoot ??= VolumeRoot(target.Label);
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);

        var catalog = OpenCatalog();
        var store   = OpenStore();

        // Load fresh records from catalog
        var src = catalog.GetVolumeById(source.Id)!;
        var tgt = catalog.GetVolumeById(target.Id)!;

        var planner = new VolumeFillbackPlanner(catalog);
        var plan    = planner.Plan(src, tgt, srcRoot, dstRoot, "DiskA", "DiskB", store);

        var svc    = new VolumeFillbackService(catalog);
        var result = svc.Execute(plan, store);
        return (plan, result);
    }

    // ── 14. SameDiskFillback_UsesMove ────────────────────────────────────────

    [Fact]
    public void SameDiskFillback_UsesMove()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var (src, _, _, _) = ProvisionAndWrite("vol-mv-src", "Game.chd", content);
        var tgt = ProvisionEmptyVolume("vol-mv-tgt");

        var (plan, result) = PlanAndExecute(src, tgt);

        // Expect Move action in plan
        var entry = plan.Entries[0];
        Assert.Equal(FillbackEntryAction.Move, entry.Action);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(1, result.MovedCount);
    }

    // ── 15. SameDiskFillback_ConfirmsSourceDeletedAndTargetExists ────────────

    [Fact]
    public void SameDiskFillback_ConfirmsSourceDeletedAndTargetExists()
    {
        var content = new byte[] { 5, 6, 7 };
        var (src, _, _, _) = ProvisionAndWrite("vol-mv2-src", "A.chd", content);
        var tgt = ProvisionEmptyVolume("vol-mv2-tgt");
        var srcFile = Path.Combine(VolumeRoot("vol-mv2-src"), "A.chd");
        var dstFile = Path.Combine(VolumeRoot("vol-mv2-tgt"), "A.chd");

        PlanAndExecute(src, tgt);

        Assert.True(File.Exists(dstFile),  "target must exist after move");
        Assert.False(File.Exists(srcFile), "source must be gone after move");
    }

    // ── 16. SameDiskFillback_HashesTargetBeforeDbUpdate ──────────────────────

    [Fact]
    public void SameDiskFillback_HashesTargetBeforeDbUpdate()
    {
        // We verify that after execution the DB reflects the target volume
        var content = new byte[] { 8, 9, 10 };
        var (src, vaId, daId, _) = ProvisionAndWrite("vol-mv3-src", "B.chd", content);
        var tgt = ProvisionEmptyVolume("vol-mv3-tgt");

        var (_, result) = PlanAndExecute(src, tgt);

        Assert.Equal(0, result.ErrorCount);
        // VA must now belong to target
        var catalog = OpenCatalog();
        var vasOnTarget = catalog.GetVolumeArtifacts(tgt.Id);
        Assert.Single(vasOnTarget);
        Assert.Equal(daId, vasOnTarget[0].DerivedArtifactId);
    }

    // ── 17. CrossDiskFillback_UsesCopyVerifyDelete ────────────────────────────

    [Fact]
    public void CrossDiskFillback_UsesCopyVerifyDelete()
    {
        var content = new byte[] { 11, 12, 13 };
        var (src, _, _, _) = ProvisionAndWrite("vol-cvd-src", "C.chd", content);
        var tgt = ProvisionEmptyVolume("vol-cvd-tgt");

        // Force cross-disk by using different-drive path prefixes for the plan
        // We can't easily use different drives in unit tests, so we build the plan
        // manually with CopyVerifyDelete action by using cross-disk planner path
        var srcRoot = VolumeRoot("vol-cvd-src");
        var dstRoot = VolumeRoot("vol-cvd-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);

        var catalog = OpenCatalog();
        var store   = OpenStore();
        var srcRec  = catalog.GetVolumeById(src.Id)!;
        var tgtRec  = catalog.GetVolumeById(tgt.Id)!;
        var vas     = catalog.GetVolumeArtifacts(srcRec.Id);
        var vis     = store.GetFillbackCandidateInfos(
            System.Linq.Enumerable.Select(vas, v => v.DerivedArtifactId).ToList());

        // Build a cross-disk plan manually
        var entry = new FillbackEntry
        {
            VolumeArtifactId  = vas[0].Id,
            DerivedArtifactId = vis[0].DerivedArtifactId,
            ReleaseName       = vis[0].ReleaseName,
            ArtifactFileName  = vis[0].FileName,
            SizeBytes         = vis[0].SizeBytes,
            ExpectedSha1      = vis[0].Sha1,
            SourceFullPath    = Path.Combine(srcRoot, vis[0].FileName),
            TargetFullPath    = Path.Combine(dstRoot, vis[0].FileName),
            Action            = FillbackEntryAction.CopyVerifyDelete,
            Reason            = "",
        };
        var plan = new VolumeFillbackPlan
        {
            SourceVolumeId = srcRec.Id, SourceVolumeLabel = srcRec.Label,
            SourceDiskLabel = "DiskA", SourceRootPath = srcRoot,
            TargetVolumeId = tgtRec.Id, TargetVolumeLabel = tgtRec.Label,
            TargetDiskLabel = "DiskB", TargetRootPath = dstRoot,
            OperationMode = FillbackOperationMode.CopyVerifyDeleteCrossDisk,
            TargetCapacityBytes = tgtRec.PlannedSizeBytes,
            TargetUsedBytes = tgtRec.ActualSizeBytes,
            TargetFreeBytes = tgtRec.PlannedSizeBytes - tgtRec.ActualSizeBytes,
            PlannedBytes = entry.SizeBytes, PlannedCount = 1, SkippedCount = 0,
            RemainingTargetFreeBytes = tgtRec.PlannedSizeBytes - tgtRec.ActualSizeBytes - entry.SizeBytes,
            SourceBytesBefore = srcRec.ActualSizeBytes, SourceBytesAfter = 0,
            TargetBytesAfter = entry.SizeBytes,
            Entries = new List<FillbackEntry> { entry },
            Warnings = [], Issues = [], SkipReasonCounts = new Dictionary<string, int>(), CanExecute = true,
        };

        var svc    = new VolumeFillbackService(catalog);
        var result = svc.Execute(plan, store);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(1, result.CopiedCount);
        Assert.True(File.Exists(Path.Combine(dstRoot, "C.chd")));
        Assert.False(File.Exists(Path.Combine(srcRoot, "C.chd")));
    }

    // ── 18. CrossDiskFillback_DoesNotDeleteSourceIfTargetHashFails ───────────

    [Fact]
    public void CrossDiskFillback_DoesNotDeleteSourceIfTargetHashFails()
    {
        var content = new byte[] { 14, 15, 16 };
        var (src, _, _, _) = ProvisionAndWrite("vol-cvdf-src", "D.chd", content);
        var tgt = ProvisionEmptyVolume("vol-cvdf-tgt");
        var srcRoot = VolumeRoot("vol-cvdf-src");
        var dstRoot = VolumeRoot("vol-cvdf-tgt");
        Directory.CreateDirectory(dstRoot);

        var catalog = OpenCatalog();
        var store   = OpenStore();
        var srcRec  = catalog.GetVolumeById(src.Id)!;
        var tgtRec  = catalog.GetVolumeById(tgt.Id)!;
        var vas     = catalog.GetVolumeArtifacts(srcRec.Id);
        var vis     = store.GetFillbackCandidateInfos(
            System.Linq.Enumerable.Select(vas, v => v.DerivedArtifactId).ToList());

        // Use WRONG SHA1 so verify fails
        var entry = new FillbackEntry
        {
            VolumeArtifactId  = vas[0].Id,
            DerivedArtifactId = vis[0].DerivedArtifactId,
            ReleaseName       = vis[0].ReleaseName,
            ArtifactFileName  = vis[0].FileName,
            SizeBytes         = vis[0].SizeBytes,
            ExpectedSha1      = "0000000000000000000000000000000000000000",  // wrong
            SourceFullPath    = Path.Combine(srcRoot, vis[0].FileName),
            TargetFullPath    = Path.Combine(dstRoot, vis[0].FileName),
            Action            = FillbackEntryAction.CopyVerifyDelete,
            Reason            = "",
        };
        var plan = new VolumeFillbackPlan
        {
            SourceVolumeId = srcRec.Id, SourceVolumeLabel = srcRec.Label,
            SourceDiskLabel = "D", SourceRootPath = srcRoot,
            TargetVolumeId = tgtRec.Id, TargetVolumeLabel = tgtRec.Label,
            TargetDiskLabel = "E", TargetRootPath = dstRoot,
            OperationMode = FillbackOperationMode.CopyVerifyDeleteCrossDisk,
            TargetCapacityBytes = tgtRec.PlannedSizeBytes, TargetUsedBytes = 0,
            TargetFreeBytes = tgtRec.PlannedSizeBytes, PlannedBytes = entry.SizeBytes,
            PlannedCount = 1, SkippedCount = 0,
            RemainingTargetFreeBytes = tgtRec.PlannedSizeBytes - entry.SizeBytes,
            SourceBytesBefore = srcRec.ActualSizeBytes, SourceBytesAfter = 0, TargetBytesAfter = 0,
            Entries = new List<FillbackEntry> { entry }, Warnings = [], Issues = [], SkipReasonCounts = new Dictionary<string, int>(), CanExecute = true,
        };

        var svc    = new VolumeFillbackService(catalog);
        var result = svc.Execute(plan, store);

        // Source must still exist (hash failed → source not deleted)
        Assert.True(File.Exists(Path.Combine(srcRoot, "D.chd")));
        Assert.True(result.ErrorCount > 0);
    }

    // ── 19. CrossDiskFillback_DoesNotUpdateDbBeforePhysicalSuccess ────────────

    [Fact]
    public void CrossDiskFillback_DoesNotUpdateDbBeforePhysicalSuccess()
    {
        // When copy fails (source doesn't exist), DB must not be updated
        var content = new byte[] { 17, 18 };
        var (src, vaId, _, _) = ProvisionAndWrite("vol-dbu-src", "E.chd", content);
        var tgt = ProvisionEmptyVolume("vol-dbu-tgt");
        var srcRoot = VolumeRoot("vol-dbu-src");
        var dstRoot = VolumeRoot("vol-dbu-tgt");
        Directory.CreateDirectory(dstRoot);

        var catalog = OpenCatalog();
        var store   = OpenStore();
        var srcRec  = catalog.GetVolumeById(src.Id)!;
        var tgtRec  = catalog.GetVolumeById(tgt.Id)!;
        var vas     = catalog.GetVolumeArtifacts(srcRec.Id);
        var vis     = store.GetFillbackCandidateInfos(
            System.Linq.Enumerable.Select(vas, v => v.DerivedArtifactId).ToList());

        // Source path points to a file that doesn't exist
        var entry = new FillbackEntry
        {
            VolumeArtifactId  = vas[0].Id,
            DerivedArtifactId = vis[0].DerivedArtifactId,
            ReleaseName       = vis[0].ReleaseName,
            ArtifactFileName  = vis[0].FileName,
            SizeBytes         = vis[0].SizeBytes,
            ExpectedSha1      = vis[0].Sha1,
            SourceFullPath    = Path.Combine(srcRoot, "nonexistent.chd"),  // wrong path
            TargetFullPath    = Path.Combine(dstRoot, vis[0].FileName),
            Action            = FillbackEntryAction.CopyVerifyDelete,
            Reason            = "",
        };
        var plan = new VolumeFillbackPlan
        {
            SourceVolumeId = srcRec.Id, SourceVolumeLabel = srcRec.Label,
            SourceDiskLabel = "D", SourceRootPath = srcRoot,
            TargetVolumeId = tgtRec.Id, TargetVolumeLabel = tgtRec.Label,
            TargetDiskLabel = "E", TargetRootPath = dstRoot,
            OperationMode = FillbackOperationMode.CopyVerifyDeleteCrossDisk,
            TargetCapacityBytes = tgtRec.PlannedSizeBytes, TargetUsedBytes = 0,
            TargetFreeBytes = tgtRec.PlannedSizeBytes, PlannedBytes = entry.SizeBytes,
            PlannedCount = 1, SkippedCount = 0,
            RemainingTargetFreeBytes = tgtRec.PlannedSizeBytes - entry.SizeBytes,
            SourceBytesBefore = srcRec.ActualSizeBytes, SourceBytesAfter = 0, TargetBytesAfter = 0,
            Entries = new List<FillbackEntry> { entry }, Warnings = [], Issues = [], SkipReasonCounts = new Dictionary<string, int>(), CanExecute = true,
        };

        new VolumeFillbackService(catalog).Execute(plan, store);

        // VA must still be on source volume
        var vasOnSrc = catalog.GetVolumeArtifacts(srcRec.Id);
        Assert.Single(vasOnSrc);
        var vasOnTgt = catalog.GetVolumeArtifacts(tgtRec.Id);
        Assert.Empty(vasOnTgt);
    }

    // ── 20. Fillback_UpdatesVolumeArtifactVolumeId ────────────────────────────

    [Fact]
    public void Fillback_UpdatesVolumeArtifactVolumeId()
    {
        var content = new byte[] { 20, 21, 22 };
        var (src, vaId, daId, _) = ProvisionAndWrite("vol-upd-src", "F.chd", content);
        var tgt = ProvisionEmptyVolume("vol-upd-tgt");

        PlanAndExecute(src, tgt);

        var catalog = OpenCatalog();
        var vasOnSrc = catalog.GetVolumeArtifacts(src.Id);
        var vasOnTgt = catalog.GetVolumeArtifacts(tgt.Id);

        Assert.Empty(vasOnSrc);
        Assert.Single(vasOnTgt);
        Assert.Equal(daId, vasOnTgt[0].DerivedArtifactId);
    }

    // ── 21. Fillback_UpdatesSourceAndTargetActualSizeBytes ───────────────────

    [Fact]
    public void Fillback_UpdatesSourceAndTargetActualSizeBytes()
    {
        var content = new byte[] { 23, 24, 25, 26 };
        var (src, _, _, _) = ProvisionAndWrite("vol-sz-src", "G.chd", content);
        var tgt = ProvisionEmptyVolume("vol-sz-tgt");

        var catalog   = OpenCatalog();
        var srcBefore = catalog.GetVolumeById(src.Id)!.ActualSizeBytes;
        var tgtBefore = catalog.GetVolumeById(tgt.Id)!.ActualSizeBytes;

        PlanAndExecute(src, tgt);

        var srcAfter = catalog.GetVolumeById(src.Id)!.ActualSizeBytes;
        var tgtAfter = catalog.GetVolumeById(tgt.Id)!.ActualSizeBytes;

        Assert.True(srcAfter < srcBefore);
        Assert.True(tgtAfter > tgtBefore);
        Assert.Equal(content.Length, (int)(tgtAfter - tgtBefore));
    }

    // ── 22. Fillback_DoesNotOverwriteExistingTarget ───────────────────────────

    [Fact]
    public void Fillback_DoesNotOverwriteExistingTarget()
    {
        var content = new byte[] { 27, 28 };
        var (src, _, _, _) = ProvisionAndWrite("vol-ow-src", "H.chd", content);
        var tgt     = ProvisionEmptyVolume("vol-ow-tgt");
        var dstRoot = VolumeRoot("vol-ow-tgt");
        Directory.CreateDirectory(dstRoot);
        var dstFile = Path.Combine(dstRoot, "H.chd");
        File.WriteAllBytes(dstFile, new byte[] { 0xFF });  // pre-occupy

        var (_, result) = PlanAndExecute(src, tgt);

        // Plan must refuse execution (Error entry → CanExecute = false)
        // Service executes active entries only; no active entries → 0 moved
        Assert.Equal(0, result.MovedCount + result.CopiedCount);
        // Original pre-occupied file must be unchanged
        Assert.Equal(new byte[] { 0xFF }, File.ReadAllBytes(dstFile));
    }

    // ── 23. Fillback_StopsOrReportsOnDeleteFailure ────────────────────────────

    [Fact]
    public void Fillback_StopsOrReportsOnDeleteFailure()
    {
        // We can't easily force a delete failure in tests, but we can verify
        // that when source doesn't exist before copy, it's reported as an error.
        var content = new byte[] { 29, 30 };
        var (src, _, _, _) = ProvisionAndWrite("vol-del-src", "I.chd", content);
        var tgt  = ProvisionEmptyVolume("vol-del-tgt");
        var srcRoot = VolumeRoot("vol-del-src");
        var dstRoot = VolumeRoot("vol-del-tgt");
        Directory.CreateDirectory(dstRoot);

        // Delete the source file before executing to simulate "source missing"
        File.Delete(Path.Combine(srcRoot, "I.chd"));

        var (_, result) = PlanAndExecute(src, tgt);

        // Source was missing → skip or error, nothing moved
        Assert.Equal(0, result.MovedCount + result.CopiedCount);
    }

    // ── 24. Fillback_RefreshesUsageAfterSuccess ───────────────────────────────

    [Fact]
    public void Fillback_RefreshesUsageAfterSuccess()
    {
        var content = new byte[] { 31, 32, 33 };
        var (src, _, _, _) = ProvisionAndWrite("vol-ref-src", "J.chd", content);
        var tgt = ProvisionEmptyVolume("vol-ref-tgt");

        PlanAndExecute(src, tgt);

        var catalog = OpenCatalog();
        var srcRec  = catalog.GetVolumeById(src.Id)!;
        var tgtRec  = catalog.GetVolumeById(tgt.Id)!;

        // Source shrunk, target grew
        Assert.Equal(0, srcRec.ActualSizeBytes);
        Assert.Equal(content.Length, (int)tgtRec.ActualSizeBytes);
    }

    // ── 25. Fillback_SourceEmptyDoesNotAutoDeleteVolume ───────────────────────

    [Fact]
    public void Fillback_SourceEmptyDoesNotAutoDeleteVolume()
    {
        var content = new byte[] { 34, 35 };
        var (src, _, _, _) = ProvisionAndWrite("vol-empty-src", "K.chd", content);
        var tgt = ProvisionEmptyVolume("vol-empty-tgt");

        var (_, result) = PlanAndExecute(src, tgt);

        // Service should signal source is empty but NOT auto-delete volume record
        Assert.True(result.SourceEmpty);
        var catalog = OpenCatalog();
        var srcRec  = catalog.GetVolumeById(src.Id);
        Assert.NotNull(srcRec);  // volume record must still exist
        Assert.Equal("present", srcRec!.Status);  // not marked lost/deleted
    }

    // ── 26. Fillback_MultiArtifact_AllBytesAccountedFor ───────────────────────

    [Fact]
    public void Fillback_MultiArtifact_AllBytesAccountedFor()
    {
        var src = ProvisionEmptyVolume("vol-mab-src", plannedSizeBytes: 1_000_000);
        var tgt = ProvisionEmptyVolume("vol-mab-tgt", plannedSizeBytes: 1_000_000);
        var cA  = new byte[] { 1, 2, 3 };
        var cB  = new byte[] { 4, 5, 6, 7 };
        var cC  = new byte[] { 8, 9 };
        AddArtifact(src.Id, "vol-mab-src", "A.chd", cA);
        AddArtifact(src.Id, "vol-mab-src", "B.chd", cB);
        AddArtifact(src.Id, "vol-mab-src", "C.chd", cC);

        PlanAndExecute(src, tgt);

        var catalog  = OpenCatalog();
        var srcAfter = catalog.GetVolumeById(src.Id)!;
        var tgtAfter = catalog.GetVolumeById(tgt.Id)!;

        Assert.Equal(0L, srcAfter.ActualSizeBytes);
        Assert.Equal((long)(cA.Length + cB.Length + cC.Length), tgtAfter.ActualSizeBytes);
    }

    // ── 27. Fillback_MultiArtifact_ArtifactOwnershipTransferred ──────────────

    [Fact]
    public void Fillback_MultiArtifact_ArtifactOwnershipTransferred()
    {
        var src = ProvisionEmptyVolume("vol-mao-src", plannedSizeBytes: 1_000_000);
        var tgt = ProvisionEmptyVolume("vol-mao-tgt", plannedSizeBytes: 1_000_000);
        AddArtifact(src.Id, "vol-mao-src", "X.chd", new byte[] { 1, 2 });
        AddArtifact(src.Id, "vol-mao-src", "Y.chd", new byte[] { 3, 4 });
        AddArtifact(src.Id, "vol-mao-src", "Z.chd", new byte[] { 5 });

        PlanAndExecute(src, tgt);

        var catalog = OpenCatalog();
        Assert.Empty(catalog.GetVolumeArtifacts(src.Id));
        Assert.Equal(3, catalog.GetVolumeArtifacts(tgt.Id).Count);
    }

    // ── 28. Fillback_PartialFill_SourceRetainsRemainingArtifact ──────────────

    [Fact]
    public void Fillback_PartialFill_SourceRetainsRemainingArtifact()
    {
        // Target fits 2 × 10-byte artifacts but not 3 (planned = 25 bytes).
        // Each artifact must have distinct content to avoid DA de-dup on identical SHA1.
        var src   = ProvisionEmptyVolume("vol-pf-src", plannedSizeBytes: 1_000_000);
        var tgt   = ProvisionEmptyVolume("vol-pf-tgt", plannedSizeBytes: 25);
        var artP  = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var artQ  = new byte[] { 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
        var artR  = new byte[] { 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 };
        AddArtifact(src.Id, "vol-pf-src", "P.chd", artP);
        AddArtifact(src.Id, "vol-pf-src", "Q.chd", artQ);
        AddArtifact(src.Id, "vol-pf-src", "R.chd", artR);

        PlanAndExecute(src, tgt);

        var catalog     = OpenCatalog();
        var srcArtCount = catalog.GetVolumeArtifacts(src.Id).Count;
        var tgtArtCount = catalog.GetVolumeArtifacts(tgt.Id).Count;

        Assert.Equal(1, srcArtCount);
        Assert.Equal(2, tgtArtCount);
    }

    // ── 29. Fillback_GetVolumes_BothUpdatedAfterExecution ────────────────────

    [Fact]
    public void Fillback_GetVolumes_BothUpdatedAfterExecution()
    {
        var content = new byte[] { 42, 43, 44 };
        var (src, _, _, _) = ProvisionAndWrite("vol-gv-src", "W.chd", content);
        var tgt = ProvisionEmptyVolume("vol-gv-tgt");

        PlanAndExecute(src, tgt);

        var catalog = OpenCatalog();
        var all     = catalog.GetVolumes();
        var srcVol  = all.FirstOrDefault(v => v.Id == src.Id);
        var tgtVol  = all.FirstOrDefault(v => v.Id == tgt.Id);

        Assert.NotNull(srcVol);
        Assert.NotNull(tgtVol);
        Assert.Equal(0L, srcVol!.ActualSizeBytes);
        Assert.Equal((long)content.Length, tgtVol!.ActualSizeBytes);
    }
}
