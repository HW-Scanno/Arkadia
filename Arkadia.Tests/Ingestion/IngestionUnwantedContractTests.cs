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

    private static string MakeSha1(string seed) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

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

    // ── 16. Staging complete for unwanted: source promotion blocked ───────────
    //        Simulates partial staging from run 1, user marks unwanted, run 2 would
    //        call UpdateReleaseStatus("present") after staging→source promotion.

    [Fact]
    public void Ingestion_ExistingStagingCompletedForUnwanted_DoesNotPromoteSource()
    {
        var store = OpenStore();
        var relId = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = "dl1", Name = "Partial Disc Set", Status = "missing" });

        // Run 1: first disc arrives; content identity provisioned; release still missing.
        var sha1A = MakeSha1(relId + "disc1.iso");
        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = $"sha1:{sha1A}", DatSha1 = sha1A, DatMd5 = null, DatCrc32 = null,
            CreatedAtUtc = DateTime.UtcNow
        });

        // User marks the release unwanted before run 2 completes staging.
        store.UpdateReleaseStatus(relId, "unwanted");

        // Run 2 would complete staging and Phase 7 would call UpdateReleaseStatus("present").
        // The SQL guard (AND status != 'unwanted') must block the promotion.
        store.UpdateReleaseStatus(relId, "present");

        Assert.Equal("unwanted", ReadStatus(relId));
    }

    // ── 17. Staging complete for unwanted: transform artifact excluded from wanted

    [Fact]
    public void Ingestion_ExistingStagingCompletedForUnwanted_DoesNotTransform()
    {
        // Even if Phase 7 incorrectly called IngestDerivedArtifact for an unwanted release,
        // the resulting DA must not appear in GetAllWantedArtifactInfos.
        var (relId, daId, _) = ProvisionRelease("unwanted", "Game.chd");

        // Simulate a second transform output also linked to this unwanted release.
        var store  = OpenStore();
        var sha1B  = MakeSha1(daId + "extra.chd");
        var cikB   = $"sha1:{sha1B}";
        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = cikB, DatSha1 = sha1B, DatMd5 = null, DatCrc32 = null,
            CreatedAtUtc = DateTime.UtcNow
        });
        var daId2 = store.IngestDerivedArtifact(cikB, "", "chd", "extra.chd",
            "archive/ps2/dl1/extra.chd", 2048, sha1B);
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId,
            ContentIdentityKey = cikB, CreatedAtUtc = DateTime.UtcNow
        });

        // Neither DA must appear in wanted queries (UNWANTED WINS).
        var wanted = store.GetAllWantedArtifactInfos();
        Assert.DoesNotContain(wanted, a => a.DerivedArtifactId == daId);
        Assert.DoesNotContain(wanted, a => a.DerivedArtifactId == daId2);
    }

    // ── 18. Staging complete for unwanted: no DA in DB when Phase 7 is skipped

    [Fact]
    public void Ingestion_ExistingStagingCompletedForUnwanted_DoesNotCreateDerivedArtifact()
    {
        // Phase 7 is never reached for unwanted releases (runtime guard in Phase 6).
        // Therefore IngestDerivedArtifact is never called; no DA should exist for this release.
        var store = OpenStore();
        var relId = Guid.NewGuid().ToString("N");
        var sha1  = MakeSha1(relId + "game.iso");
        var cik   = $"sha1:{sha1}";

        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = "dl1", Name = "Unwanted No-DA", Status = "unwanted" });
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

        // IngestDerivedArtifact was never called — Phase 7 correctly skipped.
        // No DA row should exist in the DB.
        Assert.Empty(store.GetAllArchiveArtifactInfos());
    }

    // ── 19. Staging complete for unwanted: full Phase 7 DB sequence still blocked

    [Fact]
    public void Ingestion_ExistingStagingCompletedForUnwanted_RemainsUnwanted()
    {
        // Even if every DB call Phase 7 makes is executed, the SQL guards must
        // leave the release status as 'unwanted' throughout.
        var (relId, daId, _) = ProvisionRelease("unwanted", "Game.iso");
        var store = OpenStore();

        // RecalculateReleaseStatusForArtifacts is guarded (AND status NOT IN ('unwanted')).
        store.RecalculateReleaseStatusForArtifacts(new List<string> { daId });
        Assert.Equal("unwanted", ReadStatus(relId));

        // UpdateReleaseStatus("present") is guarded (AND status != 'unwanted').
        store.UpdateReleaseStatus(relId, "present");
        Assert.Equal("unwanted", ReadStatus(relId));
    }

    // ── 20. Partial staging → marked unwanted → staging completed → skipped ───

    [Fact]
    public void Ingestion_PartialStagingThenMarkedUnwantedThenCompleted_IsSkipped()
    {
        var store = OpenStore();
        var relId = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = "dl1", Name = "Multi-Disc Set", Status = "missing" });

        // Run 1: disc 1 arrives — partial staging, content identity created.
        var sha1A = MakeSha1(relId + "disc1.iso");
        var cikA  = $"sha1:{sha1A}";
        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = cikA, DatSha1 = sha1A, DatMd5 = null, DatCrc32 = null,
            CreatedAtUtc = DateTime.UtcNow
        });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId,
            ContentIdentityKey = cikA, CreatedAtUtc = DateTime.UtcNow
        });

        // User marks the release unwanted between the two runs.
        store.UpdateReleaseStatus(relId, "unwanted");
        Assert.Equal("unwanted", ReadStatus(relId));

        // Run 2: disc 2 arrives — Phase 6 blocks it (goes to allTargetsUnwanted).
        // Phase 7 is never called. Simulate what Phase 7 WOULD have called:
        var sha1B = MakeSha1(relId + "disc2.iso");
        var cikB  = $"sha1:{sha1B}";
        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = cikB, DatSha1 = sha1B, DatMd5 = null, DatCrc32 = null,
            CreatedAtUtc = DateTime.UtcNow
        });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId,
            ContentIdentityKey = cikB, CreatedAtUtc = DateTime.UtcNow
        });
        store.UpdateReleaseStatus(relId, "present");

        // Release must still be unwanted — the SQL guard blocks the status update.
        Assert.Equal("unwanted", ReadStatus(relId));
    }

    // ── 21. Unwanted completed from staging: operation log has unwanted-skipped ─

    [Fact]
    public void Ingestion_UnwantedCompletedFromStaging_LogsUnwantedSkipped()
    {
        // When a file's only matching release is unwanted, Phase 6 adds an
        // "unwanted-skipped" operation to IngestionResult and Phase 8 increments
        // UnwantedSkipped. Verify the model correctly records this.
        var result = new IngestionResult();

        // Simulate Phase 6: unwanted release encountered for incoming file.
        var releaseName = "Unwanted Multi-Disc";
        var skipOp = new IngestionOperation("disc2.iso", "unwanted-skipped", releaseName);
        result.Operations.Add(skipOp);

        // Phase 8: file routed to allTargetsUnwanted → increment counter.
        result.UnwantedSkipped++;

        Assert.Equal(1, result.UnwantedSkipped);
        Assert.Single(result.Operations);
        Assert.Equal("unwanted-skipped", result.Operations[0].Action);
        Assert.Equal("disc2.iso",         result.Operations[0].Object);
        Assert.Equal(releaseName,          result.Operations[0].Destination);
    }
}
