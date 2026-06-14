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
/// 15 tests covering the diagnostic properties introduced in AppendVolumePlan v2:
/// TotalDerivedArtifactsForDatLine, archive physical file counts, verbose reason strings,
/// DominantReasonHint, candidate size range, and IncomingSkipIgnored guard.
/// </summary>
public sealed class AppendVolumeDiagnosticsTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _catalogDbPath;
    private readonly string _datDbPath;

    public AppendVolumeDiagnosticsTests()
    {
        _tmp           = Path.Combine(Path.GetTempPath(), "ArkDiag_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string RelPath(string fileName, string platform = "snes", string datLine = "dl1")
        => $"archive/{platform}/{datLine}/{fileName}";

    private void WriteArchiveFile(string fileName, byte[] content,
        string platform = "snes", string datLine = "dl1")
    {
        var dir = Path.Combine(_tmp, "archive", platform, datLine);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), content);
    }

    private string ProvisionWantedArtifact(string fileName, byte[] content,
        string datLine = "dl1", bool writeFile = true)
    {
        var sha1  = Sha1Hex(content);
        var cik   = $"sha1:{sha1}";
        var relId = Guid.NewGuid().ToString("N");
        var store = OpenStore();

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = datLine, Name = "Release " + fileName, Status = "present"
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
            cik, "", "chd", fileName, RelPath(fileName, "snes", datLine), content.Length, sha1);

        if (writeFile) WriteArchiveFile(fileName, content, "snes", datLine);
        return daId;
    }

    private string ProvisionUnwantedArtifact(string fileName, byte[] content, string datLine = "dl1")
    {
        var sha1  = Sha1Hex(content);
        var cik   = $"sha1:{sha1}";
        var relId = Guid.NewGuid().ToString("N");
        var store = OpenStore();

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = datLine, Name = "Unwanted " + fileName, Status = "unwanted"
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
        return store.IngestDerivedArtifact(
            cik, "", "chd", fileName, RelPath(fileName), content.Length, sha1);
    }

    private VolumeRecord ProvisionVolume(string label, string datLine = "dl1",
        long plannedSizeBytes = 100_000_000)
    {
        var catalog = OpenCatalog();
        var volId   = Guid.NewGuid().ToString("N");
        var vol     = new VolumeRecord
        {
            Id = volId, Label = label, PlatformId = "snes", DatLineId = datLine,
            Status = "present", PlannedSizeBytes = plannedSizeBytes, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        };
        catalog.SaveVolume(vol);
        return vol;
    }

    private AppendVolumePlan RunPlan(VolumeRecord vol, long? plannedBytes = null)
    {
        var volRoot = Path.Combine(_tmp, "volumes", vol.Label);
        Directory.CreateDirectory(volRoot);
        return new AppendVolumePlanner(OpenCatalog())
            .Plan(vol, volRoot, _tmp, OpenStore());
    }

    // ── 1. TotalDerivedArtifactsForDatLine counts wanted + unwanted ───────────

    [Fact]
    public void AppendPlan_DiagnosticsShowTotalDerivedArtifacts()
    {
        ProvisionWantedArtifact("Wanted.chd",   new byte[] { 1, 2, 3 });
        ProvisionUnwantedArtifact("Unwanted.chd", new byte[] { 4, 5, 6 });
        var vol  = ProvisionVolume("vol-totalda");

        var plan = RunPlan(vol);

        Assert.Equal(2, plan.TotalDerivedArtifactsForDatLine);
        Assert.Equal(1, plan.TotalCandidates);      // only the wanted one
        Assert.Equal(1, plan.ReleaseUnwantedSkipped);
    }

    // ── 2. ActiveArchivePhysicalFileCount counts physical archive files ────────

    [Fact]
    public void AppendPlan_DiagnosticsShowActiveArchivePhysicalFileCount()
    {
        ProvisionWantedArtifact("File1.chd", new byte[] { 10, 11, 12 });
        ProvisionWantedArtifact("File2.chd", new byte[] { 20, 21, 22 });
        // Third file physically in archive but not in DB
        WriteArchiveFile("Extra.chd", new byte[] { 99 });
        var vol = ProvisionVolume("vol-physcount");

        var plan = RunPlan(vol);

        Assert.Equal(3, plan.ActiveArchivePhysicalFileCount);
        Assert.Equal(2, plan.TotalCandidates);
        Assert.Equal(2, plan.PlannedCount);
    }

    // ── 3. AlreadyAssigned reason includes volume label ───────────────────────

    [Fact]
    public void AppendPlan_ReportsAlreadyAssignedCount()
    {
        var daId  = ProvisionWantedArtifact("Assigned.chd", new byte[] { 1, 2 });
        var srcVol = ProvisionVolume("vol-src-lbl");
        var tgt   = ProvisionVolume("vol-tgt-lbl");

        var sha1 = Sha1Hex(new byte[] { 1, 2 });
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

        var plan = RunPlan(tgt);

        Assert.Equal(1, plan.AlreadyAssignedSkipped);
        var entry = plan.Entries.Single(e => e.ReasonKey == AppendVolumePlanner.SkipReason.AlreadyAssigned);
        Assert.Contains("vol-src-lbl", entry.Reason);
        Assert.Contains(AppendVolumePlanner.SkipReason.AlreadyAssigned, entry.Reason);
    }

    // ── 4. ArchiveMissing reason includes expected archive path ───────────────

    [Fact]
    public void AppendPlan_ReportsArchiveMissingExpectedPath()
    {
        ProvisionWantedArtifact("Gone.chd", new byte[] { 3, 4 }, writeFile: false);
        var vol = ProvisionVolume("vol-miss-path");

        var plan = RunPlan(vol);

        Assert.Equal(1, plan.ArchiveMissingSkipped);
        var entry = plan.Entries.Single(e => e.ReasonKey == AppendVolumePlanner.SkipReason.ArchiveMissing);
        // Reason should contain the expected archive path
        Assert.Contains("archive", entry.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Gone.chd", entry.Reason);
    }

    // ── 5. TargetPathExists reason includes target path ───────────────────────

    [Fact]
    public void AppendPlan_ReportsTargetCollisionPath()
    {
        ProvisionWantedArtifact("Collision.chd", new byte[] { 5, 6 });
        var vol     = ProvisionVolume("vol-collision-path");
        var volRoot = Path.Combine(_tmp, "volumes", vol.Label);
        Directory.CreateDirectory(volRoot);
        // Pre-create the file at the target location
        File.WriteAllBytes(Path.Combine(volRoot, "Collision.chd"), new byte[] { 0xFF });

        var plan = new AppendVolumePlanner(OpenCatalog())
            .Plan(vol, volRoot, _tmp, OpenStore());

        Assert.Equal(1, plan.TargetCollisionSkipped);
        var entry = plan.Entries.Single(e => e.ReasonKey == AppendVolumePlanner.SkipReason.TargetPathExists);
        Assert.Contains("Collision.chd", entry.Reason);
        Assert.Contains("Collision.chd", entry.TargetPath);
    }

    // ── 6. TooLarge reason includes required and remaining bytes ─────────────

    [Fact]
    public void AppendPlan_ReportsTooLargeWithRemainingBytes()
    {
        var content = new byte[5000];
        ProvisionWantedArtifact("Big.chd", content);
        var vol = ProvisionVolume("vol-big", plannedSizeBytes: 10);  // only 10 bytes capacity

        var plan = RunPlan(vol);

        Assert.Equal(1, plan.TooLargeSkipped);
        var entry = plan.Entries.Single(e => e.ReasonKey == AppendVolumePlanner.SkipReason.TooLargeForRemainingTargetSpace);
        Assert.Contains(AppendVolumePlanner.SkipReason.TooLargeForRemainingTargetSpace, entry.Reason);
        // Reason must mention sizes
        Assert.Contains("needs", entry.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remaining", entry.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── 7. DominantReasonHint when all skipped as AlreadyAssigned ─────────────

    [Fact]
    public void AppendPlan_ZeroPlannedDominantReasonAlreadyAssigned()
    {
        var daId  = ProvisionWantedArtifact("Asgn.chd", new byte[] { 1, 2, 3 });
        var srcVol = ProvisionVolume("vol-hint-src");
        var tgt   = ProvisionVolume("vol-hint-tgt");

        OpenCatalog().SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = srcVol.Id,
                DatLineId = "dl1", DerivedArtifactId = daId,
                ContentIdentityKey = $"sha1:{Sha1Hex(new byte[]{1,2,3})}", Status = "present_in_final",
                AddedAtUtc = DateTime.UtcNow,
            }
        });

        var plan = RunPlan(tgt);

        Assert.Equal(0, plan.PlannedCount);
        Assert.NotEmpty(plan.DominantReasonHint);
        Assert.Contains("already assigned", plan.DominantReasonHint, StringComparison.OrdinalIgnoreCase);
    }

    // ── 8. DominantReasonHint when all skipped as ArchiveMissing ─────────────

    [Fact]
    public void AppendPlan_ZeroPlannedDominantReasonArchiveMissing()
    {
        ProvisionWantedArtifact("Missing.chd", new byte[] { 7, 8 }, writeFile: false);
        var vol = ProvisionVolume("vol-hint-miss");

        var plan = RunPlan(vol);

        Assert.Equal(0, plan.PlannedCount);
        Assert.NotEmpty(plan.DominantReasonHint);
        Assert.Contains("missing", plan.DominantReasonHint, StringComparison.OrdinalIgnoreCase);
    }

    // ── 9. DominantReasonHint when all skipped as TooLarge ───────────────────

    [Fact]
    public void AppendPlan_ZeroPlannedDominantReasonTooLarge()
    {
        ProvisionWantedArtifact("HugeFile.chd", new byte[50_000]);
        var vol = ProvisionVolume("vol-hint-large", plannedSizeBytes: 100);

        var plan = RunPlan(vol);

        Assert.Equal(0, plan.PlannedCount);
        Assert.NotEmpty(plan.DominantReasonHint);
        Assert.Contains("no remaining artifact fits", plan.DominantReasonHint, StringComparison.OrdinalIgnoreCase);
    }

    // ── 10. DominantReasonHint when all skipped as ReleaseUnwanted ───────────

    [Fact]
    public void AppendPlan_ZeroPlannedDominantReasonReleaseUnwanted()
    {
        ProvisionUnwantedArtifact("UW.chd", new byte[] { 9, 10 });
        var vol = ProvisionVolume("vol-hint-uw");

        var plan = RunPlan(vol);

        Assert.Equal(0, plan.PlannedCount);
        Assert.NotEmpty(plan.DominantReasonHint);
        Assert.Contains("UNWANTED", plan.DominantReasonHint, StringComparison.OrdinalIgnoreCase);
    }

    // ── 11. Candidate size range is reported correctly ────────────────────────

    [Fact]
    public void AppendPlan_CandidateSizeRangeReported()
    {
        var small = new byte[100];
        var large = new byte[5000];
        ProvisionWantedArtifact("Small.chd", small);
        ProvisionWantedArtifact("Large.chd", large);
        var vol = ProvisionVolume("vol-size-range", plannedSizeBytes: 100_000_000);

        var plan = RunPlan(vol);

        Assert.Equal(100 + 5000, plan.TotalCandidateBytes);
        Assert.Equal(5000,       plan.LargestCandidateBytes);
        Assert.Equal(100,        plan.SmallestCandidateBytes);
    }

    // ── 12. DA with incoming-skip relative path is excluded (IncomingSkipIgnored)

    [Fact]
    public void AppendPlan_DoesNotScanIncomingSkip()
    {
        // Provision a wanted DA whose relative_path starts with incoming-skip/
        var content = new byte[] { 42, 43, 44 };
        var sha1    = Sha1Hex(content);
        var cik     = $"sha1:{sha1}";
        var relId   = Guid.NewGuid().ToString("N");
        var store   = OpenStore();

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = "dl1", Name = "SkipGame", Status = "present"
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
        // The DA's relative_path explicitly starts with "incoming-skip/"
        store.IngestDerivedArtifact(cik, "", "chd", "SkipGame.chd",
            "incoming-skip/snes/SkipGame.chd", content.Length, sha1);

        // Write the file at the incoming-skip path so it physically exists
        var skipDir = Path.Combine(_tmp, "incoming-skip", "snes");
        Directory.CreateDirectory(skipDir);
        File.WriteAllBytes(Path.Combine(skipDir, "SkipGame.chd"), content);

        var vol  = ProvisionVolume("vol-incskip");
        var plan = RunPlan(vol);

        Assert.Equal(0, plan.PlannedCount);
        Assert.Equal(1, plan.ExcludedIncomingSkipPath);
        var entry = plan.Entries.Single();
        Assert.Equal(AppendVolumePlanner.SkipReason.IncomingSkipIgnored, entry.ReasonKey);
    }

    // ── 13. Planner continues past first skipped artifact ────────────────────

    [Fact]
    public void AppendPlan_DoesNotStopAtFirstSkippedArtifact()
    {
        // First artifact: assigned (will be skipped)
        var daId1   = ProvisionWantedArtifact("First.chd",  new byte[] { 1, 2 });
        var srcVol  = ProvisionVolume("vol-stop-src");
        // Second artifact: unassigned (should be planned)
        ProvisionWantedArtifact("Second.chd", new byte[] { 3, 4 });
        var tgt = ProvisionVolume("vol-stop-tgt");

        OpenCatalog().SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = srcVol.Id,
                DatLineId = "dl1", DerivedArtifactId = daId1,
                ContentIdentityKey = $"sha1:{Sha1Hex(new byte[]{1,2})}", Status = "present_in_final",
                AddedAtUtc = DateTime.UtcNow,
            }
        });

        var plan = RunPlan(tgt);

        Assert.Equal(1, plan.AlreadyAssignedSkipped);
        Assert.Equal(1, plan.PlannedCount);
        Assert.True(plan.CanExecute);
    }

    // ── 14. Plan exposes total-DA and archive physical count (dialog stats) ───

    [Fact]
    public void AppendDialog_DisplaysCandidatePipelineStats()
    {
        ProvisionWantedArtifact("W1.chd",   new byte[] { 10, 11 });
        ProvisionWantedArtifact("W2.chd",   new byte[] { 12, 13 });
        ProvisionUnwantedArtifact("U1.chd", new byte[] { 14, 15 });
        var vol = ProvisionVolume("vol-dialog-stats");

        var plan = RunPlan(vol);

        // Pipeline: 2 wanted + 1 unwanted = 3 total
        Assert.Equal(3, plan.TotalDerivedArtifactsForDatLine);
        Assert.Equal(2, plan.TotalCandidates);
        Assert.Equal(1, plan.ReleaseUnwantedSkipped);

        // Archive: 2 physical files (the 2 wanted ones; unwanted has no archive file)
        Assert.Equal(2, plan.ActiveArchivePhysicalFileCount);
        Assert.Equal(2, plan.ActiveArchiveKnownWantedFileCount);
        Assert.Equal(2, plan.ActiveArchiveUnassignedWantedFileCount);

        // Both planned
        Assert.Equal(2, plan.PlannedCount);
    }

    // ── 15. Skip entries expose archive source path and target path ───────────

    [Fact]
    public void AppendDialog_DisplaysReasonAndPathsForSkippedRows()
    {
        // Artifact with archive file but target collision
        var content = new byte[] { 20, 21, 22 };
        ProvisionWantedArtifact("PathCheck.chd", content);
        var vol     = ProvisionVolume("vol-dialog-paths");
        var volRoot = Path.Combine(_tmp, "volumes", vol.Label);
        Directory.CreateDirectory(volRoot);
        // Create collision
        File.WriteAllBytes(Path.Combine(volRoot, "PathCheck.chd"), new byte[] { 0xBB });

        var plan = new AppendVolumePlanner(OpenCatalog())
            .Plan(vol, volRoot, _tmp, OpenStore());

        Assert.Equal(1, plan.TargetCollisionSkipped);
        var entry = plan.Entries.Single(e => e.ReasonKey == AppendVolumePlanner.SkipReason.TargetPathExists);

        // ArchivePath must point into the archive directory
        Assert.Contains("archive", entry.ArchivePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PathCheck.chd", entry.ArchivePath);

        // TargetPath must point into the volume root
        Assert.Contains(volRoot, entry.TargetPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PathCheck.chd", entry.TargetPath);
    }
}
