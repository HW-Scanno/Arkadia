using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Arkadia.Data;
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

    // ── 13. DoesNotDeleteLocalArchiveSource ───────────────────────────────────

    [Fact]
    public void AppendExecution_DoesNotDeleteLocalArchiveSource()
    {
        var content = new byte[] { 1, 2, 3 };
        var (vol, _, _) = ProvisionArtifact("vol-nodelete", "Archive.chd", content);
        var volRoot     = VolumeRoot(vol.Label);
        var archiveSrc  = ArchivePath("Archive.chd");

        Assert.True(File.Exists(archiveSrc), "archive file must exist before execution");

        var plan   = BuildPlan(vol, volRoot);
        var result = new AppendVolumeService(OpenCatalog()).Execute(plan);

        Assert.Equal(0, result.ErrorCount);
        // Archive source must still exist after append
        Assert.True(File.Exists(archiveSrc), "archive source must NOT be deleted after append");
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
}
