using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Arkadia.Data;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Test 26: Append must never scan incoming-skip\.
///
/// Files placed in incoming-skip\ are suspended/quarantined and must not be
/// treated as archive candidates by AppendVolumePlanner.
/// This is enforced structurally: AppendVolumePlanner only reads artifacts from
/// GetAllWantedArtifactInfos (via DatLineStore) and checks their relative_path
/// against the archive directory. Files physically present in incoming-skip\
/// will never match any DB artifact's archive path, so they are never planned.
///
/// These tests verify that unwanted artifacts (which end up in incoming-skip)
/// are excluded from append planning at the DB level.
/// </summary>
public sealed class AppendIgnoresIncomingSkipTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _catalogDbPath;
    private readonly string _datDbPath;

    public AppendIgnoresIncomingSkipTests()
    {
        _tmp           = Path.Combine(Path.GetTempPath(), "ArkAIS_" + Guid.NewGuid().ToString("N")[..8]);
        _catalogDbPath = Path.Combine(_tmp, "catalog.db");
        _datDbPath     = Path.Combine(_tmp, "dat.db");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private CatalogService OpenCatalog() => new(_catalogDbPath);
    private DatLineStore   OpenStore()   => new(_datDbPath);

    private static string Sha1Hex(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// Provision an unwanted release with a file in incoming-skip (simulating
    /// what LocalArchiveRepair or Phase 8 ingestion does).
    /// </summary>
    private (VolumeRecord Vol, string DaId) ProvisionUnwantedInSkip(
        string label, string fileName, byte[] content)
    {
        var sha1  = Sha1Hex(content);
        var cik   = $"sha1:{sha1}";
        var relId = Guid.NewGuid().ToString("N");
        var volId = Guid.NewGuid().ToString("N");
        var store = OpenStore();

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = "dl1", Name = "Unwanted " + fileName, Status = "unwanted"
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

        // Write file to incoming-skip (not to archive) — simulating moved-out artifact
        var skipDir = Path.Combine(_tmp, "incoming-skip", "snes");
        Directory.CreateDirectory(skipDir);
        File.WriteAllBytes(Path.Combine(skipDir, fileName), content);

        var daId = store.IngestDerivedArtifact(cik, "", "chd", fileName,
            $"archive/snes/dl1/{fileName}", content.Length, sha1);

        var vol = new VolumeRecord
        {
            Id = volId, Label = label, PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 10_000_000, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        };
        OpenCatalog().SaveVolume(vol);

        return (vol, daId);
    }

    // ── 26. Append excludes unwanted artifacts (which reside in incoming-skip) ─

    [Fact]
    public void Append_IgnoresIncomingSkip_UnwantedArtifactsNotPlanned()
    {
        var content  = System.Text.Encoding.UTF8.GetBytes("unwanted-skipped-content");
        var (vol, _) = ProvisionUnwantedInSkip("vol-target", "Skipped.chd", content);

        var volumeRoot = Path.Combine(_tmp, "volumes", "vol-target");
        Directory.CreateDirectory(volumeRoot);
        var plan = new AppendVolumePlanner(OpenCatalog())
            .Plan(vol, volumeRoot, _tmp, OpenStore());

        // Unwanted artifact must not appear in planned entries
        Assert.Equal(0, plan.PlannedCount);
        // ReleaseUnwantedSkipped should reflect the exclusion
        Assert.Equal(1, plan.ReleaseUnwantedSkipped);
    }

    [Fact]
    public void Append_GetAllWantedArtifactInfos_ExcludesUnwantedRelease()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("excluded-content");
        var sha1    = Sha1Hex(content);
        var cik     = $"sha1:{sha1}";
        var relId   = Guid.NewGuid().ToString("N");
        var store   = OpenStore();

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = "dl1", Name = "Unwanted", Status = "unwanted"
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
        store.IngestDerivedArtifact(cik, "", "chd", "Unwanted.chd",
            "archive/snes/dl1/Unwanted.chd", content.Length, sha1);

        var wanted = store.GetAllWantedArtifactInfos();
        Assert.Empty(wanted);
    }
}
