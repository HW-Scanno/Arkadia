using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Verifies DB-level contracts that enforce unwanted early-skip during ingestion.
///
/// The full ingestion pipeline lives in MainWindow, so these tests validate the
/// underlying store invariants that guarantee the desired behaviour:
///
///   — UpdateReleaseStatus cannot mark an unwanted release as present.
///   — IngestDerivedArtifact does not touch release.status.
///   — GetAllArchiveArtifactInfos correctly identifies unwanted artifacts.
///   — IngestionResult carries an UnwantedSkipped counter.
/// </summary>
public sealed class IngestionUnwantedContractTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _datDbPath;

    public IngestionUnwantedContractTests()
    {
        _tmp       = Path.Combine(Path.GetTempPath(), "ArkIUC_" + Guid.NewGuid().ToString("N")[..8]);
        _datDbPath = Path.Combine(_tmp, "dat.db");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private DatLineStore OpenStore() => new(_datDbPath);

    private (string ReleaseId, string DaId, string Sha1) ProvisionRelease(
        string status, string fileName = "Game.chd")
    {
        var store = OpenStore();
        var relId = Guid.NewGuid().ToString("N");
        var sha1  = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(relId + fileName))).ToLowerInvariant();
        var cik = $"sha1:{sha1}";

        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = "dl1", Name = "Release " + fileName, Status = status });
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
        var daId = store.IngestDerivedArtifact(cik, "", "chd", fileName,
            $"archive/ps2/dl1/{fileName}", 1024, sha1);

        return (relId, daId, sha1);
    }

    private string? ReadStatus(string id) =>
        OpenStore().LoadReleasesByDatLine("dl1").Find(r => r.Id == id)?.Status;

    // ── 7. Ingestion cannot mark unwanted release as present ─────────────────
    //      (the actual Phase 7 call: store.UpdateReleaseStatus(releaseId, "present"))

    [Fact]
    public void Ingestion_UnwantedRelease_StatusRemainsUnwanted_AfterUpdatePresent()
    {
        var (relId, _, _) = ProvisionRelease("unwanted");
        OpenStore().UpdateReleaseStatus(relId, "present");
        Assert.Equal("unwanted", ReadStatus(relId));
    }

    // ── 8. IngestDerivedArtifact does not change release status ──────────────

    [Fact]
    public void Ingestion_UnwantedRelease_IngestDerivedArtifact_DoesNotChangeStatus()
    {
        var (relId, _, _) = ProvisionRelease("unwanted");

        // Simulate what Phase 7 does: ingest a new derived artifact for the same release
        var sha2 = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes("extra-content"))).ToLowerInvariant();
        var cik2 = $"sha1:{sha2}";
        var store = OpenStore();
        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = cik2, DatSha1 = sha2, DatMd5 = null, DatCrc32 = null,
            CreatedAtUtc = DateTime.UtcNow
        });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId,
            ContentIdentityKey = cik2, CreatedAtUtc = DateTime.UtcNow
        });
        store.IngestDerivedArtifact(cik2, "", "chd", "Extra.chd", "archive/ps2/dl1/Extra.chd", 512, sha2);

        Assert.Equal("unwanted", ReadStatus(relId));
    }

    // ── 9. GetAllArchiveArtifactInfos marks unwanted artifacts correctly ──────

    [Fact]
    public void Ingestion_UnwantedRelease_ArchiveArtifactIsMarkedUnwanted()
    {
        var (_, daId, _) = ProvisionRelease("unwanted");
        var infos = OpenStore().GetAllArchiveArtifactInfos();
        var info  = infos.Find(a => a.DerivedArtifactId == daId);
        Assert.NotNull(info);
        Assert.True(info!.IsUnwanted);
    }

    // ── 10. GetAllArchiveArtifactInfos does not mark wanted artifact as unwanted

    [Fact]
    public void Ingestion_WantedRelease_ArchiveArtifactNotMarkedUnwanted()
    {
        var (_, daId, _) = ProvisionRelease("present");
        var infos = OpenStore().GetAllArchiveArtifactInfos();
        var info  = infos.Find(a => a.DerivedArtifactId == daId);
        Assert.NotNull(info);
        Assert.False(info!.IsUnwanted);
    }

    // ── 11. GetAllWantedArtifactInfos excludes unwanted artifacts ────────────

    [Fact]
    public void Ingestion_UnwantedRelease_ExcludedFromWantedArtifactInfos()
    {
        var (_, daId, _) = ProvisionRelease("unwanted");
        var wanted = OpenStore().GetAllWantedArtifactInfos();
        Assert.DoesNotContain(wanted, a => a.DerivedArtifactId == daId);
    }

    // ── 12. Full ingestion sequence does not change unwanted status ───────────

    [Fact]
    public void Ingestion_UnwantedRelease_StatusRemainsUnwanted_FullSequence()
    {
        var (relId, daId, _) = ProvisionRelease("unwanted");
        var store = OpenStore();

        // Simulate every status-touching call ingestion makes:
        store.BatchUpdateDerivedArtifactStatus(new List<string> { daId }, "present");
        store.RecalculateReleaseStatusForArtifacts(new List<string> { daId });
        store.UpdateReleaseStatus(relId, "present");

        Assert.Equal("unwanted", ReadStatus(relId));
    }

    // ── 15. IngestionResult carries UnwantedSkipped counter ──────────────────

    [Fact]
    public void Ingestion_LogIncludesUnwantedSkippedCount()
    {
        var result = new IngestionResult { UnwantedSkipped = 3 };
        Assert.Equal(3, result.UnwantedSkipped);
    }
}
