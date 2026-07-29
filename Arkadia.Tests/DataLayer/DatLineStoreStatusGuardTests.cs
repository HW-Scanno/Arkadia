using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

/// <summary>
/// Guards that protect unwanted release status from being overwritten by ingestion
/// or status-recalculation code paths.
/// </summary>
public sealed class DatLineStoreStatusGuardTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _datDbPath;

    public DatLineStoreStatusGuardTests()
    {
        _tmp       = Path.Combine(Path.GetTempPath(), "ArkDSG_" + Guid.NewGuid().ToString("N")[..8]);
        _datDbPath = Path.Combine(_tmp, "dat.db");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private DatLineStore OpenStore() => new(_datDbPath);

    private (string ReleaseId, string DaId) ProvisionRelease(
        string status, string fileName = "Game.chd")
    {
        var store  = OpenStore();
        var relId  = Guid.NewGuid().ToString("N");
        var sha1   = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(fileName + relId))).ToLowerInvariant();
        var cik    = $"sha1:{sha1}";

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = "dl1", Name = "Test Release", Status = status
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
        var daId = store.IngestDerivedArtifact(
            cik, "", "chd", fileName,
            $"archive/snes/dl1/{fileName}", 1024, sha1);

        return (relId, daId);
    }

    private string? ReadReleaseStatus(string releaseId)
    {
        var releases = OpenStore().LoadReleasesByDatLine("dl1");
        foreach (var r in releases)
            if (r.Id == releaseId) return r.Status;
        return null;
    }

    // ── 1. UpdateReleaseStatus does not reset unwanted to present ─────────────

    [Fact]
    public void Ingestion_DoesNotResetUnwantedToPresent()
    {
        var (relId, _) = ProvisionRelease("unwanted");

        // Simulate what ingestion does unconditionally after processing a release
        OpenStore().UpdateReleaseStatus(relId, "present");

        Assert.Equal("unwanted", ReadReleaseStatus(relId));
    }

    // ── 2. Deriving an artifact does not change an unwanted release status ─────

    [Fact]
    public void Ingestion_DerivedCommitPreservesUnwantedStatus()
    {
        var (relId, _) = ProvisionRelease("unwanted");

        // IngestDerivedArtifact is called during ingestion — it should not touch release.status
        var sha1 = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes("extra"))).ToLowerInvariant();
        var cik2 = $"sha1:{sha1}";
        OpenStore().EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = cik2, DatSha1 = sha1,
            DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow
        });
        OpenStore().IngestDerivedArtifact(cik2, "", "chd", "Extra.chd",
            "archive/snes/dl1/Extra.chd", 512, sha1);

        // Then ingestion calls UpdateReleaseStatus("present") — must be blocked
        OpenStore().UpdateReleaseStatus(relId, "present");

        Assert.Equal("unwanted", ReadReleaseStatus(relId));
    }

    // ── 3. UpdateReleaseStatus does not override unwanted for any status ──────
    //
    // The guard is now universal: no generic lifecycle update may leave unwanted.
    // Only RestoreWantedRelease is allowed to do so.

    [Fact]
    public void UpdateReleaseStatus_Present_DoesNotOverrideUnwanted()
    {
        var (relId, _) = ProvisionRelease("unwanted");

        // Ingestion trying to set present — must be blocked
        OpenStore().UpdateReleaseStatus(relId, "present");
        Assert.Equal("unwanted", ReadReleaseStatus(relId));

        // Only RestoreWantedRelease can leave the unwanted state
        OpenStore().RestoreWantedRelease(relId);
        Assert.Equal("missing", ReadReleaseStatus(relId));
    }

    // ── 4. RecalculateReleaseStatusForArtifacts cannot change unwanted ─────────

    [Fact]
    public void RecalculateStatus_DoesNotChangeUnwanted()
    {
        var (relId, daId) = ProvisionRelease("unwanted");

        // Mark the artifact as present so recalculation would normally set release to present
        OpenStore().BatchUpdateDerivedArtifactStatus(new List<string> { daId }, "present");
        int changed = OpenStore().RecalculateReleaseStatusForArtifacts(new List<string> { daId });

        // The guard "status NOT IN (..., 'unwanted')" must have blocked the update
        Assert.Equal(0, changed);
        Assert.Equal("unwanted", ReadReleaseStatus(relId));
    }

    // ── 10. Analytics: unwanted exclusion survives simulated ingestion ─────────

    [Fact]
    public void Analytics_UnwantedStillExcludedAfterIngestion()
    {
        var (relId, daId) = ProvisionRelease("unwanted");

        // Simulate the full ingestion status-write sequence
        OpenStore().BatchUpdateDerivedArtifactStatus(new List<string> { daId }, "present");
        OpenStore().RecalculateReleaseStatusForArtifacts(new List<string> { daId });
        OpenStore().UpdateReleaseStatus(relId, "present");

        // Release must still be unwanted
        Assert.Equal("unwanted", ReadReleaseStatus(relId));

        // GetAllWantedArtifactInfos must not include this artifact
        var wanted = OpenStore().GetAllWantedArtifactInfos();
        Assert.Empty(wanted);

        // GetUnwantedArtifactCount must report 1
        Assert.Equal(1, OpenStore().GetUnwantedArtifactCount());
    }
}
