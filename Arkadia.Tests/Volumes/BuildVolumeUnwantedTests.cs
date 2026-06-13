using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Tests that GetPlanningCandidates (used by Build Volume / Plan Volume) excludes
/// releases and artifacts linked to any unwanted release.
/// </summary>
public sealed class BuildVolumeUnwantedTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _datDbPath;
    private readonly string _appRoot;

    public BuildVolumeUnwantedTests()
    {
        _tmp       = Path.Combine(Path.GetTempPath(), "ArkBVU_" + Guid.NewGuid().ToString("N")[..8]);
        _datDbPath = Path.Combine(_tmp, "dat.db");
        _appRoot   = Path.Combine(_tmp, "approot");
        Directory.CreateDirectory(_tmp);
        Directory.CreateDirectory(_appRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private DatLineStore OpenStore() => new(_datDbPath);

    private static string Sha1Hex(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private string ProvisionRelease(string status, string fileName, byte[] content)
    {
        var sha1  = Sha1Hex(content);
        var cik   = $"sha1:{sha1}";
        var relId = Guid.NewGuid().ToString("N");
        var store = OpenStore();

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = "dl1", Name = "Release " + fileName, Status = status
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

        // Write physical archive file
        var archiveDir = Path.Combine(_appRoot, "archive", "ps2", "dl1");
        Directory.CreateDirectory(archiveDir);
        File.WriteAllBytes(Path.Combine(archiveDir, fileName), content);

        store.IngestDerivedArtifact(cik, "", "chd", fileName,
            $"archive/ps2/dl1/{fileName}", content.Length, sha1);

        return relId;
    }

    // ── 27. BuildVolume excludes unwanted releases ────────────────────────────

    [Fact]
    public void BuildVolume_ExcludesUnwantedReleases()
    {
        var unwantedId = ProvisionRelease("unwanted", "Unwanted.chd",
            System.Text.Encoding.UTF8.GetBytes("unwanted-data"));
        var presentId  = ProvisionRelease("present",  "Present.chd",
            System.Text.Encoding.UTF8.GetBytes("present-data"));

        var candidates = OpenStore().GetPlanningCandidates(_appRoot, new HashSet<string>());

        var ids = candidates.Select(c => c.ReleaseId).ToList();
        Assert.DoesNotContain(unwantedId, ids);
        Assert.Contains(presentId,  ids);
    }

    [Fact]
    public void BuildVolume_ExcludesArtifactsLinkedToAnyUnwantedRelease()
    {
        // Shared artifact: linked to both a present and an unwanted release
        var content = System.Text.Encoding.UTF8.GetBytes("shared-content");
        var sha1    = Sha1Hex(content);
        var cik     = $"sha1:{sha1}";
        var store   = OpenStore();

        // Wanted release
        var wantedId = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = wantedId, DatLineId = "dl1", Name = "Wanted", Status = "present" });

        // Unwanted release
        var unwantedId = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = unwantedId, DatLineId = "dl1", Name = "Unwanted", Status = "unwanted" });

        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null,
            CreatedAtUtc = DateTime.UtcNow
        });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = wantedId,
            ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow
        });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = unwantedId,
            ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow
        });

        var archiveDir = Path.Combine(_appRoot, "archive", "ps2", "dl1");
        Directory.CreateDirectory(archiveDir);
        File.WriteAllBytes(Path.Combine(archiveDir, "Shared.chd"), content);
        store.IngestDerivedArtifact(cik, "", "chd", "Shared.chd",
            "archive/ps2/dl1/Shared.chd", content.Length, sha1);

        var candidates = store.GetPlanningCandidates(_appRoot, new HashSet<string>());

        // UNWANTED WINS: shared artifact excludes both releases
        Assert.DoesNotContain(candidates, c => c.ReleaseId == wantedId);
        Assert.DoesNotContain(candidates, c => c.ReleaseId == unwantedId);
    }

    [Fact]
    public void BuildVolume_OnlyWantedReleasesReturnedAsCandidates()
    {
        ProvisionRelease("unwanted", "U1.chd", System.Text.Encoding.UTF8.GetBytes("u1"));
        ProvisionRelease("unwanted", "U2.chd", System.Text.Encoding.UTF8.GetBytes("u2"));
        ProvisionRelease("present",  "P1.chd", System.Text.Encoding.UTF8.GetBytes("p1"));

        var candidates = OpenStore().GetPlanningCandidates(_appRoot, new HashSet<string>());

        Assert.Single(candidates);
        Assert.DoesNotContain(candidates, c => c.ReleaseName.Contains("U1") || c.ReleaseName.Contains("U2"));
    }
}
