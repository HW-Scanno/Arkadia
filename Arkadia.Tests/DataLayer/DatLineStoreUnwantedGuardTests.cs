using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

/// <summary>
/// Tests for the strengthened unwanted-status guard:
/// only RestoreWantedRelease may remove a release from the unwanted state.
/// </summary>
public sealed class DatLineStoreUnwantedGuardTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _datDbPath;

    public DatLineStoreUnwantedGuardTests()
    {
        _tmp       = Path.Combine(Path.GetTempPath(), "ArkUGT_" + Guid.NewGuid().ToString("N")[..8]);
        _datDbPath = Path.Combine(_tmp, "dat.db");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private DatLineStore OpenStore() => new(_datDbPath);

    private string ProvisionUnwanted()
    {
        var relId = Guid.NewGuid().ToString("N");
        OpenStore().UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = "dl1", Name = "Test", Status = "unwanted"
        });
        return relId;
    }

    private string? ReadStatus(string id) =>
        OpenStore().LoadReleasesByDatLine("dl1").Find(r => r.Id == id)?.Status;

    // ── 1. UpdateReleaseStatus does not change unwanted → present ─────────────

    [Fact]
    public void UpdateReleaseStatus_DoesNotChangeUnwantedToPresent()
    {
        var id = ProvisionUnwanted();
        OpenStore().UpdateReleaseStatus(id, "present");
        Assert.Equal("unwanted", ReadStatus(id));
    }

    // ── 2. UpdateReleaseStatus does not change unwanted → missing ─────────────

    [Fact]
    public void UpdateReleaseStatus_DoesNotChangeUnwantedToMissing()
    {
        var id = ProvisionUnwanted();
        OpenStore().UpdateReleaseStatus(id, "missing");
        Assert.Equal("unwanted", ReadStatus(id));
    }

    // ── 3. UpdateReleaseStatus does not change unwanted → lost ───────────────

    [Fact]
    public void UpdateReleaseStatus_DoesNotChangeUnwantedToLost()
    {
        var id = ProvisionUnwanted();
        OpenStore().UpdateReleaseStatus(id, "lost");
        Assert.Equal("unwanted", ReadStatus(id));
    }

    // ── 4. UpdateReleaseStatus does not change unwanted → outdated ────────────

    [Fact]
    public void UpdateReleaseStatus_DoesNotChangeUnwantedToOutdated()
    {
        var id = ProvisionUnwanted();
        OpenStore().UpdateReleaseStatus(id, "outdated");
        Assert.Equal("unwanted", ReadStatus(id));
    }

    // ── 5. UpdateReleaseStatus does not change unwanted → pending ─────────────

    [Fact]
    public void UpdateReleaseStatus_DoesNotChangeUnwantedToPending()
    {
        var id = ProvisionUnwanted();
        OpenStore().UpdateReleaseStatus(id, "pending");
        Assert.Equal("unwanted", ReadStatus(id));
    }

    // ── 6. RestoreWantedRelease is the only allowed way to leave unwanted ──────

    [Fact]
    public void RestoreWantedRelease_IsOnlyAllowedWayToLeaveUnwanted()
    {
        var id = ProvisionUnwanted();

        // Generic update is blocked
        OpenStore().UpdateReleaseStatus(id, "missing");
        Assert.Equal("unwanted", ReadStatus(id));

        // Dedicated restore works
        OpenStore().RestoreWantedRelease(id);
        Assert.Equal("missing", ReadStatus(id));
    }

    // ── 7. RestoreWantedRelease sets show_in_catalog = true ───────────────────

    [Fact]
    public void RestoreWantedRelease_SetsShowInCatalogTrue()
    {
        var id = ProvisionUnwanted();
        var store = OpenStore();
        store.SetShowInCatalog(id, false);
        store.RestoreWantedRelease(id);

        var rec = OpenStore().LoadReleasesByDatLine("dl1").Find(r => r.Id == id);
        Assert.NotNull(rec);
        Assert.True(rec!.ShowInCatalog);
    }

    // ── 8. RecalculateReleaseStatusForArtifacts still cannot change unwanted ──

    [Fact]
    public void RecalculateStatus_StillDoesNotChangeUnwanted()
    {
        var relId = Guid.NewGuid().ToString("N");
        var sha1  = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(relId))).ToLowerInvariant();
        var cik  = $"sha1:{sha1}";
        var store = OpenStore();

        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = "dl1", Name = "R", Status = "unwanted" });
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
        var daId = store.IngestDerivedArtifact(cik, "", "chd", "R.chd", $"archive/p/dl1/R.chd", 512, sha1);

        store.BatchUpdateDerivedArtifactStatus(new List<string> { daId }, "present");
        store.RecalculateReleaseStatusForArtifacts(new List<string> { daId });

        Assert.Equal("unwanted", ReadStatus(relId));
    }
}
