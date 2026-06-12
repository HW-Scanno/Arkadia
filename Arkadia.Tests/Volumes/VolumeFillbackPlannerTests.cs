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
/// Tests for VolumeFillbackPlanner — the dry-run planning service.
///
/// Each test provisions real SQLite stores + a temp filesystem and exercises
/// VolumeFillbackPlanner.Plan() directly without touching any files during planning.
/// </summary>
public sealed class VolumeFillbackPlannerTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _catalogDbPath;
    private readonly string _datDbPath;

    public VolumeFillbackPlannerTests()
    {
        _tmp           = Path.Combine(Path.GetTempPath(), "ArkFBPlan_" + Guid.NewGuid().ToString("N")[..8]);
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
    /// Creates catalog + DAT-line DB records for one artifact on one volume.
    /// Returns (VolumeRecord, vaId, daId).
    /// </summary>
    private (VolumeRecord Vol, string VaId, string DaId) ProvisionArtifact(
        string volLabel, string fileName, byte[] content,
        string relName = "Test Release", string relStatus = "present",
        string datLineId = "dl1", long plannedSizeBytes = 1_000_000)
    {
        var catalog = OpenCatalog();
        var store   = OpenStore();
        var sha1    = Sha1Hex(content);
        var cik     = $"sha1:{sha1}";
        var relId   = Guid.NewGuid().ToString("N");
        var volId   = Guid.NewGuid().ToString("N");
        var vaId    = Guid.NewGuid().ToString("N");

        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = datLineId, Name = relName, Status = relStatus });
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
            cik, "", "chd", fileName, $"archive/snes/{datLineId}/{fileName}", content.Length, sha1);

        catalog.SaveVolume(new VolumeRecord
        {
            Id = volId, Label = volLabel, PlatformId = "snes", DatLineId = datLineId,
            Status = "present", PlannedSizeBytes = plannedSizeBytes, ActualSizeBytes = content.Length,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = vaId, VolumeId = volId, DatLineId = datLineId,
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            }
        });

        return (
            new VolumeRecord
            {
                Id = volId, Label = volLabel, PlatformId = "snes", DatLineId = datLineId,
                Status = "present", PlannedSizeBytes = plannedSizeBytes, ActualSizeBytes = content.Length,
                CreatedAt = DateTime.UtcNow, Health = "ok",
            }, vaId, daId);
    }

    /// <summary>Creates a bare (empty) volume record in the catalog.</summary>
    private VolumeRecord ProvisionEmptyVolume(string volLabel,
        string datLineId = "dl1", long plannedSizeBytes = 50_000_000)
    {
        var catalog = OpenCatalog();
        var volId   = Guid.NewGuid().ToString("N");
        var vol = new VolumeRecord
        {
            Id = volId, Label = volLabel, PlatformId = "snes", DatLineId = datLineId,
            Status = "present", PlannedSizeBytes = plannedSizeBytes, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        };
        catalog.SaveVolume(vol);
        return vol;
    }

    private VolumeFillbackPlan RunPlan(
        VolumeRecord source, VolumeRecord target,
        string? srcRoot = null, string? dstRoot = null,
        string srcDiskLabel = "DiskA", string dstDiskLabel = "DiskB")
    {
        srcRoot ??= VolumeRoot(source.Label);
        dstRoot ??= VolumeRoot(target.Label);
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);

        var planner = new VolumeFillbackPlanner(OpenCatalog());
        return planner.Plan(source, target, srcRoot, dstRoot, srcDiskLabel, dstDiskLabel, OpenStore());
    }

    // ── 1. RejectsSameSourceAndTarget ────────────────────────────────────────

    [Fact]
    public void FillbackPlan_RejectsSameSourceAndTarget()
    {
        var (src, _, _) = ProvisionArtifact("vol-same-src", "A.chd", new byte[] { 1, 2, 3 });
        var root = VolumeRoot("vol-same-src");
        Directory.CreateDirectory(root);
        WriteVolumeFile("vol-same-src", "A.chd", new byte[] { 1, 2, 3 });

        var planner = new VolumeFillbackPlanner(OpenCatalog());
        var plan = planner.Plan(src, src, root, root, "D", "D", OpenStore());

        Assert.False(plan.CanExecute);
        Assert.NotEmpty(plan.Issues);
    }

    // ── 2. RequiresMountedSource ──────────────────────────────────────────────

    [Fact]
    public void FillbackPlan_RequiresMountedSource()
    {
        var (src, _, _) = ProvisionArtifact("vol-mts-src", "A.chd", new byte[] { 1 });
        var tgt         = ProvisionEmptyVolume("vol-mts-tgt");
        var dstRoot     = VolumeRoot("vol-mts-tgt");
        Directory.CreateDirectory(dstRoot);

        // Source root does NOT exist
        var planner = new VolumeFillbackPlanner(OpenCatalog());
        var plan = planner.Plan(src, tgt, "/does/not/exist", dstRoot, "D", "E", OpenStore());

        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Issues, i => i.Contains("Source"));
    }

    // ── 3. RequiresMountedTarget ──────────────────────────────────────────────

    [Fact]
    public void FillbackPlan_RequiresMountedTarget()
    {
        var (src, _, _) = ProvisionArtifact("vol-mtt-src", "B.chd", new byte[] { 2 });
        var tgt         = ProvisionEmptyVolume("vol-mtt-tgt");
        var srcRoot     = VolumeRoot("vol-mtt-src");
        Directory.CreateDirectory(srcRoot);

        var planner = new VolumeFillbackPlanner(OpenCatalog());
        var plan = planner.Plan(src, tgt, srcRoot, "/does/not/exist", "D", "E", OpenStore());

        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Issues, i => i.Contains("Target"));
    }

    // ── 4. RequiresSameDatLine ────────────────────────────────────────────────

    [Fact]
    public void FillbackPlan_RequiresSameDatLine()
    {
        var (src, _, _) = ProvisionArtifact("vol-sdl-src", "C.chd", new byte[] { 3 }, datLineId: "dl1");
        var tgt         = ProvisionEmptyVolume("vol-sdl-tgt", datLineId: "dl2");
        var srcRoot     = VolumeRoot("vol-sdl-src");
        var dstRoot     = VolumeRoot("vol-sdl-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-sdl-src", "C.chd", new byte[] { 3 });

        var planner = new VolumeFillbackPlanner(OpenCatalog());
        var plan = planner.Plan(src, tgt, srcRoot, dstRoot, "D", "E", OpenStore());

        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Issues, i => i.Contains("DAT line"));
    }

    // ── 5. OrdersCandidatesAlphabetically ────────────────────────────────────

    [Fact]
    public void FillbackPlan_OrdersCandidatesAlphabetically()
    {
        // Three artifacts: Zebra, Alpha, Mango — expect Alpha, Mango, Zebra
        var contentA = new byte[] { 10, 20 };
        var contentM = new byte[] { 30, 40 };
        var contentZ = new byte[] { 50, 60 };

        var (srcA, _, _) = ProvisionArtifact("vol-ord-src", "Zebra.chd",  contentZ, relName: "Zebra");
        var catalog      = OpenCatalog();
        var store        = OpenStore();
        var sha1A        = Sha1Hex(contentA);
        var sha1M        = Sha1Hex(contentM);
        var cikA = $"sha1:{sha1A}"; var cikM = $"sha1:{sha1M}";
        var relAlphaId   = Guid.NewGuid().ToString("N");
        var relMangoId   = Guid.NewGuid().ToString("N");

        store.UpsertRelease(new ReleaseRecord { Id = relAlphaId, DatLineId = "dl1", Name = "Alpha", Status = "present" });
        store.UpsertRelease(new ReleaseRecord { Id = relMangoId, DatLineId = "dl1", Name = "Mango", Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cikA, DatSha1 = sha1A, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cikM, DatSha1 = sha1M, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = relAlphaId, ContentIdentityKey = cikA, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = relMangoId, ContentIdentityKey = cikM, CreatedAtUtc = DateTime.UtcNow });
        var daAlpha = store.IngestDerivedArtifact(cikA, "", "chd", "Alpha.chd", "archive/snes/dl1/Alpha.chd", contentA.Length, sha1A);
        var daMango = store.IngestDerivedArtifact(cikM, "", "chd", "Mango.chd", "archive/snes/dl1/Mango.chd", contentM.Length, sha1M);

        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = srcA.Id, DatLineId = "dl1", DerivedArtifactId = daAlpha, ContentIdentityKey = cikA, Status = "present_in_final", AddedAtUtc = DateTime.UtcNow },
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = srcA.Id, DatLineId = "dl1", DerivedArtifactId = daMango, ContentIdentityKey = cikM, Status = "present_in_final", AddedAtUtc = DateTime.UtcNow },
        });

        // Reload source with updated actual size
        var srcRecord = catalog.GetVolumeById(srcA.Id)!;
        var tgt       = ProvisionEmptyVolume("vol-ord-tgt", plannedSizeBytes: 10_000_000);
        var srcRoot   = VolumeRoot("vol-ord-src");
        var dstRoot   = VolumeRoot("vol-ord-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-ord-src", "Zebra.chd",  contentZ);
        WriteVolumeFile("vol-ord-src", "Alpha.chd",  contentA);
        WriteVolumeFile("vol-ord-src", "Mango.chd",  contentM);

        var planner = new VolumeFillbackPlanner(catalog);
        var plan    = planner.Plan(srcRecord, catalog.GetVolumeById(tgt.Id)!, srcRoot, dstRoot, "D", "D", store);

        var planned = plan.Entries.Where(e => e.Action is FillbackEntryAction.Move or FillbackEntryAction.CopyVerifyDelete).ToList();
        Assert.True(planned.Count >= 2);
        // Alpha must come before Mango, Mango before Zebra
        var names = planned.Select(e => e.ArtifactFileName).ToList();
        Assert.True(names.IndexOf("Alpha.chd") < names.IndexOf("Mango.chd"));
        Assert.True(names.IndexOf("Mango.chd") < names.IndexOf("Zebra.chd"));
    }

    // ── 6. ExcludesUnwanted ───────────────────────────────────────────────────

    [Fact]
    public void FillbackPlan_ExcludesUnwanted()
    {
        var content     = new byte[] { 11, 22, 33 };
        var (src, _, _) = ProvisionArtifact("vol-excl-src", "Bad.chd", content, relStatus: "unwanted");
        var tgt         = ProvisionEmptyVolume("vol-excl-tgt");
        var srcRoot     = VolumeRoot("vol-excl-src");
        var dstRoot     = VolumeRoot("vol-excl-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-excl-src", "Bad.chd", content);

        var plan = RunPlan(src, tgt);

        // Unwanted release must produce nothing to execute
        Assert.False(plan.CanExecute);
        Assert.Equal(0, plan.PlannedCount);
    }

    // ── 7. ExcludesLostOrMissing ──────────────────────────────────────────────

    [Fact]
    public void FillbackPlan_ExcludesLostOrMissing()
    {
        var content = new byte[] { 44, 55 };
        // Provision artifact but do NOT write the physical file → source not found → Skip
        var (src, _, _) = ProvisionArtifact("vol-exlm-src", "Missing.chd", content);
        var tgt         = ProvisionEmptyVolume("vol-exlm-tgt");
        var srcRoot     = VolumeRoot("vol-exlm-src");
        var dstRoot     = VolumeRoot("vol-exlm-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        // NOT writing the file — simulates missing/lost artifact

        var plan = RunPlan(src, tgt);

        Assert.False(plan.CanExecute);
        Assert.Equal(0, plan.PlannedCount);
        Assert.True(plan.SkippedCount >= 1);
    }

    // ── 8. UsesFlatTargetPath ─────────────────────────────────────────────────

    [Fact]
    public void FillbackPlan_UsesFlatTargetPath()
    {
        var content     = new byte[] { 66, 77 };
        var (src, _, _) = ProvisionArtifact("vol-flat-src", "Flat.chd", content);
        var tgt         = ProvisionEmptyVolume("vol-flat-tgt");
        var srcRoot     = VolumeRoot("vol-flat-src");
        var dstRoot     = VolumeRoot("vol-flat-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-flat-src", "Flat.chd", content);

        var plan = RunPlan(src, tgt);

        var planned = plan.Entries.First(e => e.Action is FillbackEntryAction.Move or FillbackEntryAction.CopyVerifyDelete);
        // Target path must be: <target root>\Flat.chd  (flat — no subfolder)
        Assert.Equal(Path.Combine(dstRoot, "Flat.chd"), planned.TargetFullPath);
        Assert.Equal(Path.GetDirectoryName(planned.TargetFullPath), dstRoot);
    }

    // ── 9. DoesNotOverwriteTarget ─────────────────────────────────────────────

    [Fact]
    public void FillbackPlan_DoesNotOverwriteTarget()
    {
        var content     = new byte[] { 88, 99 };
        var (src, _, _) = ProvisionArtifact("vol-nov-src", "Dup.chd", content);
        var tgt         = ProvisionEmptyVolume("vol-nov-tgt");
        var srcRoot     = VolumeRoot("vol-nov-src");
        var dstRoot     = VolumeRoot("vol-nov-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-nov-src", "Dup.chd", content);
        // Pre-occupy the target path with DIFFERENT content
        File.WriteAllBytes(Path.Combine(dstRoot, "Dup.chd"), new byte[] { 0xFF });

        var plan = RunPlan(src, tgt);

        // Must not plan an overwrite — entry must be Error
        var entry = plan.Entries.First(e => e.ArtifactFileName == "Dup.chd");
        Assert.Equal(FillbackEntryAction.Error, entry.Action);
        Assert.False(plan.CanExecute);
    }

    // ── 10. SkipsTooLargeArtifactAndContinues ────────────────────────────────

    [Fact]
    public void FillbackPlan_SkipsTooLargeArtifactAndContinues()
    {
        // Two artifacts: Large (won't fit) and Small (fits)
        // Target capacity exactly fits Small but not Large
        var contentLarge = new byte[5000];
        var contentSmall = new byte[] { 1, 2 };

        var (src, _, _) = ProvisionArtifact("vol-cap-src", "Large.chd", contentLarge, relName: "AAA Large");
        var catalog     = OpenCatalog();
        var store       = OpenStore();

        // Add Small artifact to source volume
        var sha1S = Sha1Hex(contentSmall);
        var cikS  = $"sha1:{sha1S}";
        var relSId = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = relSId, DatLineId = "dl1", Name = "ZZZ Small", Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cikS, DatSha1 = sha1S, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = relSId, ContentIdentityKey = cikS, CreatedAtUtc = DateTime.UtcNow });
        var daSmall = store.IngestDerivedArtifact(cikS, "", "chd", "Small.chd", "archive/snes/dl1/Small.chd", contentSmall.Length, sha1S);
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = src.Id, DatLineId = "dl1", DerivedArtifactId = daSmall, ContentIdentityKey = cikS, Status = "present_in_final", AddedAtUtc = DateTime.UtcNow },
        });

        // Target: capacity only fits Small (100 bytes)
        var tgtId = Guid.NewGuid().ToString("N");
        catalog.SaveVolume(new VolumeRecord
        {
            Id = tgtId, Label = "vol-cap-tgt", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 100, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        var tgt = catalog.GetVolumeById(tgtId)!;

        var srcRoot = VolumeRoot("vol-cap-src");
        var dstRoot = VolumeRoot("vol-cap-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-cap-src", "Large.chd", contentLarge);
        WriteVolumeFile("vol-cap-src", "Small.chd", contentSmall);

        var srcRecord = catalog.GetVolumeById(src.Id)!;
        var planner   = new VolumeFillbackPlanner(catalog);
        var plan      = planner.Plan(srcRecord, tgt, srcRoot, dstRoot, "D", "D", store);

        // Large must be skipped (too large); Small must be planned
        Assert.True(plan.PlannedCount >= 1);
        Assert.True(plan.SkippedCount >= 1);
        var smallEntry = plan.Entries.FirstOrDefault(e => e.ArtifactFileName == "Small.chd");
        Assert.NotNull(smallEntry);
        Assert.True(smallEntry!.Action is FillbackEntryAction.Move or FillbackEntryAction.CopyVerifyDelete);
        var largeEntry = plan.Entries.FirstOrDefault(e => e.ArtifactFileName == "Large.chd");
        Assert.NotNull(largeEntry);
        Assert.Equal(FillbackEntryAction.Skip, largeEntry!.Action);
    }

    // ── 11. CalculatesSourceAndTargetAfterBytes ───────────────────────────────

    [Fact]
    public void FillbackPlan_CalculatesSourceAndTargetAfterBytes()
    {
        var content     = new byte[] { 1, 2, 3, 4, 5 };
        var (src, _, _) = ProvisionArtifact("vol-calc-src", "Data.chd", content);
        var tgt         = ProvisionEmptyVolume("vol-calc-tgt");
        var srcRoot     = VolumeRoot("vol-calc-src");
        var dstRoot     = VolumeRoot("vol-calc-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-calc-src", "Data.chd", content);

        var catalog   = OpenCatalog();
        var srcRecord = catalog.GetVolumeById(src.Id)!;
        var tgtRecord = catalog.GetVolumeById(tgt.Id)!;
        var planner   = new VolumeFillbackPlanner(catalog);
        var plan      = planner.Plan(srcRecord, tgtRecord, srcRoot, dstRoot, "D", "E", OpenStore());

        Assert.Equal(srcRecord.ActualSizeBytes,                 plan.SourceBytesBefore);
        Assert.Equal(srcRecord.ActualSizeBytes - plan.PlannedBytes, plan.SourceBytesAfter);
        Assert.Equal(tgtRecord.ActualSizeBytes + plan.PlannedBytes, plan.TargetBytesAfter);
    }

    // ── 12. DetectsSameDiskMoveMode ───────────────────────────────────────────

    [Fact]
    public void FillbackPlan_DetectsSameDiskMoveMode()
    {
        var content     = new byte[] { 11, 22 };
        var (src, _, _) = ProvisionArtifact("vol-smd-src", "G.chd", content);
        var tgt         = ProvisionEmptyVolume("vol-smd-tgt");
        var srcRoot     = VolumeRoot("vol-smd-src");
        var dstRoot     = VolumeRoot("vol-smd-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-smd-src", "G.chd", content);

        // Both paths are under _tmp (same drive on Windows)
        var plan = RunPlan(src, tgt, srcRoot, dstRoot);

        // Paths share the same drive root → Move mode
        Assert.Equal(FillbackOperationMode.MoveSameDisk, plan.OperationMode);
        var entry = plan.Entries.First(e => e.ArtifactFileName == "G.chd");
        Assert.Equal(FillbackEntryAction.Move, entry.Action);
    }

    // ── 13. DetectsCrossDiskCopyMode ─────────────────────────────────────────

    [Fact]
    public void FillbackPlan_DetectsCrossDiskCopyMode()
    {
        // IsSameDisk compares Path.GetPathRoot; test it directly
        Assert.False(VolumeFillbackPlanner.IsSameDisk(@"D:\VolA", @"E:\VolB"));
        Assert.True(VolumeFillbackPlanner.IsSameDisk(@"D:\VolA", @"D:\VolB"));
        Assert.True(VolumeFillbackPlanner.IsSameDisk(@"D:\VolA\sub", @"D:\VolB\sub"));
    }

    // ── 14. PlansSmallFileWhenTargetHasFreeSpace ──────────────────────────────

    [Fact]
    public void FillbackPlan_PlansSmallFileWhenTargetHasFreeSpace()
    {
        var content     = new byte[] { 1, 2, 3 };
        var (src, _, _) = ProvisionArtifact("vol-pfr-src", "Game.chd", content, plannedSizeBytes: 1_000);
        var tgt         = ProvisionEmptyVolume("vol-pfr-tgt", plannedSizeBytes: 1_000_000);
        WriteVolumeFile("vol-pfr-src", "Game.chd", content);

        var plan = RunPlan(src, tgt);

        Assert.True(plan.CanExecute);
        Assert.Equal(1, plan.PlannedCount);
        Assert.Equal(0, plan.SkippedCount);
    }

    // ── 15. PlansMultipleSmallFilesUntilCapacity ──────────────────────────────

    [Fact]
    public void FillbackPlan_PlansMultipleSmallFilesUntilCapacity()
    {
        var cA = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };   // 10 bytes
        var cB = new byte[] { 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
        var cC = new byte[] { 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 };

        var (src, _, _) = ProvisionArtifact("vol-mul-src", "AA.chd", cA, relName: "AA", plannedSizeBytes: 100);
        var catalog     = OpenCatalog();
        var store       = OpenStore();

        void AddArtifact(string fileName, string relName, byte[] content)
        {
            var sha1 = Sha1Hex(content);
            var cik  = $"sha1:{sha1}";
            var rId  = Guid.NewGuid().ToString("N");
            store.UpsertRelease(new ReleaseRecord { Id = rId, DatLineId = "dl1", Name = relName, Status = "present" });
            store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
            store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = rId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });
            var daId = store.IngestDerivedArtifact(cik, "", "chd", fileName, $"archive/snes/dl1/{fileName}", content.Length, sha1);
            catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
            {
                new() { Id = Guid.NewGuid().ToString("N"), VolumeId = src.Id, DatLineId = "dl1", DerivedArtifactId = daId, ContentIdentityKey = cik, Status = "present_in_final", AddedAtUtc = DateTime.UtcNow },
            });
        }

        AddArtifact("BB.chd", "BB", cB);
        AddArtifact("CC.chd", "CC", cC);

        // Target: 20 bytes free → fits exactly 2 of the 3 × 10-byte files
        var tgtId = Guid.NewGuid().ToString("N");
        catalog.SaveVolume(new VolumeRecord
        {
            Id = tgtId, Label = "vol-mul-tgt", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 20, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        var srcRoot   = VolumeRoot("vol-mul-src");
        var dstRoot   = VolumeRoot("vol-mul-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-mul-src", "AA.chd", cA);
        WriteVolumeFile("vol-mul-src", "BB.chd", cB);
        WriteVolumeFile("vol-mul-src", "CC.chd", cC);

        var srcRecord = catalog.GetVolumeById(src.Id)!;
        var tgtRecord = catalog.GetVolumeById(tgtId)!;
        var planner   = new VolumeFillbackPlanner(catalog);
        var plan      = planner.Plan(srcRecord, tgtRecord, srcRoot, dstRoot, "D", "D", store);

        Assert.Equal(2, plan.PlannedCount);
        Assert.Equal(1, plan.SkippedCount);
        Assert.True(plan.CanExecute);
    }

    // ── 16. SkipsTooLargeAndContinuesToSmallerLaterArtifact ──────────────────

    [Fact]
    public void FillbackPlan_SkipsTooLargeAndContinuesToSmallerLaterArtifact()
    {
        // "AAA" artifact is alphabetically first and too large;
        // "ZZZ" artifact fits — planner must continue after skipping large one.
        var contentLarge = new byte[1000];
        var contentSmall = new byte[] { 1, 2 };

        var (src, _, _) = ProvisionArtifact("vol-sl2-src", "AAA-large.chd", contentLarge,
            relName: "AAA Large", plannedSizeBytes: 2000);
        var catalog = OpenCatalog();
        var store   = OpenStore();

        var sha1S = Sha1Hex(contentSmall);
        var cikS  = $"sha1:{sha1S}";
        var relId = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = "dl1", Name = "ZZZ Small", Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cikS, DatSha1 = sha1S, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cikS, CreatedAtUtc = DateTime.UtcNow });
        var daSmall = store.IngestDerivedArtifact(cikS, "", "chd", "ZZZ-small.chd", "archive/snes/dl1/ZZZ-small.chd", contentSmall.Length, sha1S);
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = src.Id, DatLineId = "dl1", DerivedArtifactId = daSmall, ContentIdentityKey = cikS, Status = "present_in_final", AddedAtUtc = DateTime.UtcNow },
        });

        var tgtId = Guid.NewGuid().ToString("N");
        catalog.SaveVolume(new VolumeRecord
        {
            Id = tgtId, Label = "vol-sl2-tgt", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 10, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        var srcRoot   = VolumeRoot("vol-sl2-src");
        var dstRoot   = VolumeRoot("vol-sl2-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-sl2-src", "AAA-large.chd", contentLarge);
        WriteVolumeFile("vol-sl2-src", "ZZZ-small.chd", contentSmall);

        var srcRecord = catalog.GetVolumeById(src.Id)!;
        var tgtRecord = catalog.GetVolumeById(tgtId)!;
        var planner   = new VolumeFillbackPlanner(catalog);
        var plan      = planner.Plan(srcRecord, tgtRecord, srcRoot, dstRoot, "D", "D", store);

        Assert.Equal(1, plan.PlannedCount);
        Assert.Equal(1, plan.SkippedCount);
        var largeEntry = plan.Entries.First(e => e.ArtifactFileName == "AAA-large.chd");
        Assert.Equal(FillbackEntryAction.Skip, largeEntry.Action);
        Assert.Contains(VolumeFillbackPlanner.SkipReason.TooLargeForRemainingTargetSpace, largeEntry.Reason);
        var smallEntry = plan.Entries.First(e => e.ArtifactFileName == "ZZZ-small.chd");
        Assert.True(smallEntry.Action is FillbackEntryAction.Move or FillbackEntryAction.CopyVerifyDelete);
    }

    // ── 17. ZeroPlannedIncludesSkipReasons ───────────────────────────────────

    [Fact]
    public void FillbackPlan_ZeroPlannedIncludesSkipReasons()
    {
        // Two artifacts provisioned in DB but NEITHER written to disk → both SourceFileMissing
        var cA = new byte[] { 1, 2, 3 };
        var cB = new byte[] { 4, 5, 6 };

        var (src, _, _) = ProvisionArtifact("vol-zpr-src", "A.chd", cA, relName: "AA", plannedSizeBytes: 100_000);
        var catalog     = OpenCatalog();
        var store       = OpenStore();

        var sha1B = Sha1Hex(cB);
        var cikB  = $"sha1:{sha1B}";
        var relId = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = "dl1", Name = "BB", Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cikB, DatSha1 = sha1B, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cikB, CreatedAtUtc = DateTime.UtcNow });
        var daB = store.IngestDerivedArtifact(cikB, "", "chd", "B.chd", "archive/snes/dl1/B.chd", cB.Length, sha1B);
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = src.Id, DatLineId = "dl1", DerivedArtifactId = daB, ContentIdentityKey = cikB, Status = "present_in_final", AddedAtUtc = DateTime.UtcNow },
        });

        var tgt     = ProvisionEmptyVolume("vol-zpr-tgt", plannedSizeBytes: 1_000_000);
        var srcRoot = VolumeRoot("vol-zpr-src");
        var dstRoot = VolumeRoot("vol-zpr-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        // Files NOT written to disk

        var srcRecord = catalog.GetVolumeById(src.Id)!;
        var tgtRecord = catalog.GetVolumeById(tgt.Id)!;
        var planner   = new VolumeFillbackPlanner(catalog);
        var plan      = planner.Plan(srcRecord, tgtRecord, srcRoot, dstRoot, "D", "E", store);

        Assert.Equal(0, plan.PlannedCount);
        Assert.True(plan.SkipReasonCounts.ContainsKey(VolumeFillbackPlanner.SkipReason.SourceFileMissing));
        Assert.Equal(2, plan.SkipReasonCounts[VolumeFillbackPlanner.SkipReason.SourceFileMissing]);
    }

    // ── 18. UsesFlatSourcePath ────────────────────────────────────────────────

    [Fact]
    public void FillbackPlan_UsesFlatSourcePath()
    {
        var content     = new byte[] { 10, 20, 30 };
        var (src, _, _) = ProvisionArtifact("vol-fsp-src", "Game.chd", content);
        var tgt         = ProvisionEmptyVolume("vol-fsp-tgt");
        var srcRoot     = VolumeRoot("vol-fsp-src");
        var dstRoot     = VolumeRoot("vol-fsp-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-fsp-src", "Game.chd", content);  // flat: no subdirectory

        var plan = RunPlan(src, tgt, srcRoot, dstRoot);

        var entry = plan.Entries.First(e => e.ArtifactFileName == "Game.chd");
        Assert.Equal(Path.Combine(srcRoot, "Game.chd"), entry.SourceFullPath);
        Assert.Equal(srcRoot, Path.GetDirectoryName(entry.SourceFullPath));
    }

    // ── 19. SourceFileMissingReasonVisible ────────────────────────────────────

    [Fact]
    public void FillbackPlan_SourceFileMissingReasonVisible()
    {
        var content     = new byte[] { 5, 10, 15 };
        var (src, _, _) = ProvisionArtifact("vol-smr-src", "Ghost.chd", content);
        var tgt         = ProvisionEmptyVolume("vol-smr-tgt");
        var srcRoot     = VolumeRoot("vol-smr-src");
        var dstRoot     = VolumeRoot("vol-smr-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        // File NOT written — simulates non-flat layout or genuinely missing file

        var plan = RunPlan(src, tgt, srcRoot, dstRoot);

        var entry = plan.Entries.First(e => e.ArtifactFileName == "Ghost.chd");
        Assert.Equal(FillbackEntryAction.Skip, entry.Action);
        Assert.Equal(VolumeFillbackPlanner.SkipReason.SourceFileMissing, entry.Reason);
    }

    // ── 20. TargetCollisionReasonVisible ─────────────────────────────────────

    [Fact]
    public void FillbackPlan_TargetCollisionReasonVisible()
    {
        var content     = new byte[] { 7, 14, 21 };
        var (src, _, _) = ProvisionArtifact("vol-tcr-src", "Clash.chd", content);
        var tgt         = ProvisionEmptyVolume("vol-tcr-tgt");
        var srcRoot     = VolumeRoot("vol-tcr-src");
        var dstRoot     = VolumeRoot("vol-tcr-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        WriteVolumeFile("vol-tcr-src", "Clash.chd", content);
        // Pre-occupy target path with DIFFERENT content → unknown collision
        File.WriteAllBytes(Path.Combine(dstRoot, "Clash.chd"), new byte[] { 0xFF, 0xFE });

        var plan = RunPlan(src, tgt, srcRoot, dstRoot);

        var entry = plan.Entries.First(e => e.ArtifactFileName == "Clash.chd");
        Assert.Equal(FillbackEntryAction.Error, entry.Action);
        Assert.Contains(VolumeFillbackPlanner.SkipReason.TargetCollision, entry.Reason);
        Assert.False(plan.CanExecute);
    }

    // ── 21. DiagnosticsIncludeSkippedReasonCounts ─────────────────────────────

    [Fact]
    public void FillbackPlan_DiagnosticsIncludeSkippedReasonCounts()
    {
        // One artifact physically missing (SourceFileMissing),
        // one artifact too large (TooLargeForRemainingTargetSpace).
        var cMissing = new byte[] { 1, 2, 3 };
        var cLarge   = new byte[5000];

        var (src, _, _) = ProvisionArtifact("vol-drc-src", "AA-missing.chd", cMissing,
            relName: "AA Missing", plannedSizeBytes: 10_000);
        var catalog = OpenCatalog();
        var store   = OpenStore();

        var sha1L = Sha1Hex(cLarge);
        var cikL  = $"sha1:{sha1L}";
        var relId = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = "dl1", Name = "BB Large", Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cikL, DatSha1 = sha1L, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cikL, CreatedAtUtc = DateTime.UtcNow });
        var daLarge = store.IngestDerivedArtifact(cikL, "", "chd", "BB-large.chd", "archive/snes/dl1/BB-large.chd", cLarge.Length, sha1L);
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = src.Id, DatLineId = "dl1", DerivedArtifactId = daLarge, ContentIdentityKey = cikL, Status = "present_in_final", AddedAtUtc = DateTime.UtcNow },
        });

        // Target: 10 bytes free — large artifact won't fit
        var tgtId = Guid.NewGuid().ToString("N");
        catalog.SaveVolume(new VolumeRecord
        {
            Id = tgtId, Label = "vol-drc-tgt", PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 10, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });

        var srcRoot = VolumeRoot("vol-drc-src");
        var dstRoot = VolumeRoot("vol-drc-tgt");
        Directory.CreateDirectory(srcRoot);
        Directory.CreateDirectory(dstRoot);
        // AA-missing.chd: NOT written → SourceFileMissing
        WriteVolumeFile("vol-drc-src", "BB-large.chd", cLarge);  // exists but too large

        var srcRecord = catalog.GetVolumeById(src.Id)!;
        var tgtRecord = catalog.GetVolumeById(tgtId)!;
        var planner   = new VolumeFillbackPlanner(catalog);
        var plan      = planner.Plan(srcRecord, tgtRecord, srcRoot, dstRoot, "D", "D", store);

        Assert.Equal(0, plan.PlannedCount);
        Assert.True(plan.SkipReasonCounts.ContainsKey(VolumeFillbackPlanner.SkipReason.SourceFileMissing));
        Assert.True(plan.SkipReasonCounts.ContainsKey(VolumeFillbackPlanner.SkipReason.TooLargeForRemainingTargetSpace));
        Assert.Equal(1, plan.SkipReasonCounts[VolumeFillbackPlanner.SkipReason.SourceFileMissing]);
        Assert.Equal(1, plan.SkipReasonCounts[VolumeFillbackPlanner.SkipReason.TooLargeForRemainingTargetSpace]);
    }
}
