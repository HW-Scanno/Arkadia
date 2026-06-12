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
/// Tests for AppendVolumePlanner.
///
/// Each test provisions real SQLite stores + a temp filesystem and exercises
/// AppendVolumePlanner.Plan() directly.
/// </summary>
public sealed class AppendVolumePlannerTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _catalogDbPath;
    private readonly string _datDbPath;

    public AppendVolumePlannerTests()
    {
        _tmp           = Path.Combine(Path.GetTempPath(), "ArkAppPlan_" + Guid.NewGuid().ToString("N")[..8]);
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

    private void WriteArchiveFile(string fileName, byte[] content, string datLineId = "dl1")
    {
        var dir = Path.Combine(_tmp, "archive", "snes", datLineId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), content);
    }

    private static string RelPath(string fileName, string datLineId = "dl1")
        => $"archive/snes/{datLineId}/{fileName}";

    /// <summary>
    /// Provisions one artifact in both catalog + DAT-line store, writes archive file,
    /// and returns the VolumeRecord and daId.
    /// </summary>
    private (VolumeRecord Vol, string DaId) ProvisionArtifact(
        string volLabel, string fileName, byte[] content,
        string relStatus = "present", string datLineId = "dl1",
        long plannedSizeBytes = 10_000_000, bool writeArchiveFile = true)
    {
        var catalog = OpenCatalog();
        var store   = OpenStore();

        var sha1  = Sha1Hex(content);
        var cik   = $"sha1:{sha1}";
        var relId = Guid.NewGuid().ToString("N");
        var volId = Guid.NewGuid().ToString("N");
        var vaId  = Guid.NewGuid().ToString("N");

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = datLineId, Name = "Test Release " + fileName, Status = relStatus
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

        if (writeArchiveFile)
            WriteArchiveFile(fileName, content, datLineId);

        return (vol, daId);
    }

    /// <summary>Provisions an empty target volume with no artifacts.</summary>
    private VolumeRecord ProvisionEmptyVolume(string label,
        string datLineId = "dl1", long plannedSizeBytes = 10_000_000)
    {
        var catalog = OpenCatalog();
        var volId   = Guid.NewGuid().ToString("N");
        var vol     = new VolumeRecord
        {
            Id = volId, Label = label, PlatformId = "snes", DatLineId = datLineId,
            Status = "present", PlannedSizeBytes = plannedSizeBytes, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        };
        catalog.SaveVolume(vol);
        return vol;
    }

    private AppendVolumePlan RunPlan(VolumeRecord volume,
        string? volRoot = null, string? archiveRoot = null)
    {
        volRoot     ??= VolumeRoot(volume.Label);
        archiveRoot ??= _tmp;
        Directory.CreateDirectory(volRoot);
        return new AppendVolumePlanner(OpenCatalog())
            .Plan(volume, volRoot, archiveRoot, OpenStore());
    }

    // ── 1. FindsUnassignedLocalArchiveArtifact ────────────────────────────────

    [Fact]
    public void AppendPlan_FindsUnassignedLocalArchiveArtifact()
    {
        // Archive artifact exists, not assigned to any volume → should be planned
        var content = new byte[] { 1, 2, 3, 4 };
        var (_, _)  = ProvisionArtifact("vol-src", "Game.chd", content);
        var target  = ProvisionEmptyVolume("vol-target");

        var plan = RunPlan(target);

        Assert.Equal(1, plan.TotalCandidates);
        Assert.Equal(1, plan.PlannedCount);
        Assert.True(plan.CanExecute);
    }

    // ── 2. DoesNotRequireNewIngest ────────────────────────────────────────────

    [Fact]
    public void AppendPlan_DoesNotRequireNewIngest()
    {
        // Archive artifact was ingested earlier, not newly ingested — still a candidate
        var content = new byte[] { 5, 6, 7 };
        ProvisionArtifact("vol-old", "OldGame.chd", content);
        var target = ProvisionEmptyVolume("vol-target2");

        var plan = RunPlan(target);

        // Must find the old artifact — not limited to recent ingest
        Assert.True(plan.PlannedCount >= 1);
    }

    // ── 3. ExcludesAlreadyAssignedArtifacts ──────────────────────────────────

    [Fact]
    public void AppendPlan_ExcludesAlreadyAssignedArtifacts()
    {
        var content = new byte[] { 10, 20, 30 };
        var (srcVol, daId) = ProvisionArtifact("vol-assigned-src", "Assigned.chd", content);

        // Assign the artifact to srcVol in catalog
        var catalog = OpenCatalog();
        var store   = OpenStore();
        var sha1    = Sha1Hex(content);
        var cik     = $"sha1:{sha1}";
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = srcVol.Id,
                DatLineId = "dl1", DerivedArtifactId = daId,
                ContentIdentityKey = cik, Status = "present_in_final",
                AddedAtUtc = DateTime.UtcNow,
            }
        });

        var target = ProvisionEmptyVolume("vol-target3");
        var plan   = RunPlan(target);

        Assert.Equal(0, plan.PlannedCount);
        Assert.True(plan.AlreadyAssignedSkipped >= 1);
        Assert.True(plan.SkipReasonCounts.ContainsKey(AppendVolumePlanner.SkipReason.AlreadyAssigned));
        Assert.False(plan.CanExecute);
    }

    // ── 4. ExcludesUnwanted ───────────────────────────────────────────────────

    [Fact]
    public void AppendPlan_ExcludesUnwanted()
    {
        // Unwanted release → GetAllWantedArtifactInfos excludes it → TotalCandidates = 0
        ProvisionArtifact("vol-uw-src", "Unwanted.chd", new byte[] { 1, 2 },
            relStatus: "unwanted");
        var target = ProvisionEmptyVolume("vol-uw-target");

        var plan = RunPlan(target);

        Assert.Equal(0, plan.TotalCandidates);
        Assert.Equal(0, plan.PlannedCount);
        Assert.False(plan.CanExecute);
    }

    // ── 5. RequiresPhysicalArchiveFile ────────────────────────────────────────

    [Fact]
    public void AppendPlan_RequiresPhysicalArchiveFile()
    {
        // Artifact in DB but NO archive file on disk → skip with ArchiveMissing
        ProvisionArtifact("vol-miss-src", "NoFile.chd", new byte[] { 3, 4 },
            writeArchiveFile: false);
        var target = ProvisionEmptyVolume("vol-miss-target");

        var plan = RunPlan(target);

        Assert.Equal(0, plan.PlannedCount);
        Assert.True(plan.ArchiveMissingSkipped >= 1);
        Assert.True(plan.SkipReasonCounts.ContainsKey(AppendVolumePlanner.SkipReason.ArchiveMissing));
    }

    // ── 6. UsesFlatTargetPath ─────────────────────────────────────────────────

    [Fact]
    public void AppendPlan_UsesFlatTargetPath()
    {
        var content = new byte[] { 5, 6, 7, 8 };
        ProvisionArtifact("vol-flat-src", "Flat.chd", content);
        var target  = ProvisionEmptyVolume("vol-flat-target");
        var volRoot = VolumeRoot(target.Label);

        var plan = RunPlan(target, volRoot);

        var entry = plan.Entries[0];
        Assert.Equal(AppendEntryAction.Copy, entry.Action);
        Assert.Equal(Path.Combine(volRoot, "Flat.chd"), entry.TargetPath);
        Assert.Equal(volRoot, Path.GetDirectoryName(entry.TargetPath));
    }

    // ── 7. DoesNotOverwriteExistingTarget ─────────────────────────────────────

    [Fact]
    public void AppendPlan_DoesNotOverwriteExistingTarget()
    {
        var content = new byte[] { 9, 10, 11 };
        ProvisionArtifact("vol-overw-src", "Existing.chd", content);
        var target  = ProvisionEmptyVolume("vol-overw-target");
        var volRoot = VolumeRoot(target.Label);
        Directory.CreateDirectory(volRoot);
        // Pre-existing file at target
        File.WriteAllBytes(Path.Combine(volRoot, "Existing.chd"), new byte[] { 0xFF });

        var plan = RunPlan(target, volRoot);

        Assert.Equal(0, plan.PlannedCount);
        Assert.True(plan.TargetCollisionSkipped >= 1);
        Assert.True(plan.SkipReasonCounts.ContainsKey(AppendVolumePlanner.SkipReason.TargetPathExists));
    }

    // ── 8. SkipsTooLargeAndContinuesToSmaller ────────────────────────────────

    [Fact]
    public void AppendPlan_SkipsTooLargeAndContinuesToSmaller()
    {
        // Large (5000 bytes) and Small (2 bytes). Target capacity = 10 bytes (only Small fits).
        var contentLarge = new byte[5000];
        var contentSmall = new byte[] { 1, 2 };

        var store   = OpenStore();
        var catalog = OpenCatalog();

        // Provision large artifact
        var sha1L  = Sha1Hex(contentLarge);
        var cikL   = $"sha1:{sha1L}";
        var relL   = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = relL, DatLineId = "dl1", Name = "AAA Large", Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cikL, DatSha1 = sha1L, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = relL, ContentIdentityKey = cikL, CreatedAtUtc = DateTime.UtcNow });
        store.IngestDerivedArtifact(cikL, "", "chd", "AAA-large.chd", RelPath("AAA-large.chd"), contentLarge.Length, sha1L);
        WriteArchiveFile("AAA-large.chd", contentLarge);

        // Provision small artifact
        var sha1S  = Sha1Hex(contentSmall);
        var cikS   = $"sha1:{sha1S}";
        var relS   = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = relS, DatLineId = "dl1", Name = "ZZZ Small", Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cikS, DatSha1 = sha1S, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = relS, ContentIdentityKey = cikS, CreatedAtUtc = DateTime.UtcNow });
        store.IngestDerivedArtifact(cikS, "", "chd", "ZZZ-small.chd", RelPath("ZZZ-small.chd"), contentSmall.Length, sha1S);
        WriteArchiveFile("ZZZ-small.chd", contentSmall);

        // Target: 10 bytes free → large won't fit, small will
        var volId = Guid.NewGuid().ToString("N");
        catalog.SaveVolume(new VolumeRecord
        {
            Id = volId, Label = "vol-cap-target", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 10, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        var target = catalog.GetVolumeById(volId)!;

        var plan = RunPlan(target);

        Assert.Equal(1, plan.PlannedCount);
        Assert.True(plan.TooLargeSkipped >= 1);
        var largeEntry = plan.Entries.FirstOrDefault(e => e.FileName == "AAA-large.chd");
        Assert.NotNull(largeEntry);
        Assert.Equal(AppendEntryAction.Skip, largeEntry!.Action);
        Assert.Contains(AppendVolumePlanner.SkipReason.TooLargeForRemainingTargetSpace, largeEntry.Reason);
        var smallEntry = plan.Entries.FirstOrDefault(e => e.FileName == "ZZZ-small.chd");
        Assert.NotNull(smallEntry);
        Assert.Equal(AppendEntryAction.Copy, smallEntry!.Action);
    }

    // ── 9. ZeroCandidatesReportsAlreadyAssigned ───────────────────────────────

    [Fact]
    public void AppendPlan_ZeroCandidatesReportsAlreadyAssigned()
    {
        var content      = new byte[] { 20, 21 };
        var (srcVol, daId) = ProvisionArtifact("vol-aa-src", "Assigned2.chd", content);

        var sha1 = Sha1Hex(content);
        var cik  = $"sha1:{sha1}";
        OpenCatalog().SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = srcVol.Id,
                DatLineId = "dl1", DerivedArtifactId = daId,
                ContentIdentityKey = cik, Status = "present_in_final",
                AddedAtUtc = DateTime.UtcNow,
            }
        });

        var target = ProvisionEmptyVolume("vol-aa-target");
        var plan   = RunPlan(target);

        Assert.Equal(0, plan.PlannedCount);
        Assert.Equal(1, plan.AlreadyAssignedSkipped);
        Assert.Equal(1, plan.SkipReasonCounts[AppendVolumePlanner.SkipReason.AlreadyAssigned]);
    }

    // ── 10. ZeroCandidatesReportsUnwantedSkipped ──────────────────────────────

    [Fact]
    public void AppendPlan_ZeroCandidatesReportsUnwantedSkipped()
    {
        // All artifacts belong to unwanted releases → TotalCandidates = 0
        ProvisionArtifact("vol-uw2-src", "Uw.chd", new byte[] { 1, 2 }, relStatus: "unwanted");
        var target = ProvisionEmptyVolume("vol-uw2-target");

        var plan = RunPlan(target);

        Assert.Equal(0, plan.TotalCandidates);
        Assert.Equal(0, plan.PlannedCount);
    }

    // ── 11. ZeroCandidatesReportsArchiveMissing ───────────────────────────────

    [Fact]
    public void AppendPlan_ZeroCandidatesReportsArchiveMissing()
    {
        // Artifact in DB but archive file deleted/missing
        ProvisionArtifact("vol-am-src", "Gone.chd", new byte[] { 5, 6 },
            writeArchiveFile: false);
        var target = ProvisionEmptyVolume("vol-am-target");

        var plan = RunPlan(target);

        Assert.Equal(0, plan.PlannedCount);
        Assert.Equal(1, plan.ArchiveMissingSkipped);
        Assert.True(plan.SkipReasonCounts[AppendVolumePlanner.SkipReason.ArchiveMissing] >= 1);
    }
}
