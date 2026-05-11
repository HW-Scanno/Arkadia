using System;
using System.IO;
using System.Linq;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Amp;

public sealed class AmpExportPlanServiceTests : IDisposable
{
    private readonly string         _baseDir;
    private readonly string         _dataDir;
    private readonly CatalogService _catalog;

    private const string HwFamilyId = "snes";
    private const string DatLineId  = "snes-nointro";
    private const string SystemName = "Super Nintendo";
    private const string ReleaseId1 = "rel-001";
    private const string ReleaseId2 = "rel-002";
    private const string RelName1   = "Super Mario World (USA)";
    private const string RelName2   = "Donkey Kong Country (USA)";

    public AmpExportPlanServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_baseDir, "data");
        Directory.CreateDirectory(_dataDir);
        _catalog = new CatalogService(_baseDir);
        RegisterCatalogEntries();
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // ── Setup helpers ──────────────────────────────────────────────────────────

    private void RegisterCatalogEntries()
    {
        _catalog.SaveHardwareFamily(new HardwareFamilyRecord
            { Id = HwFamilyId, Name = SystemName });

        var dbRelPath = $"systems/{HwFamilyId}/{DatLineId}.db";
        Directory.CreateDirectory(Path.Combine(_dataDir, "systems", HwFamilyId));
        _catalog.SaveDatLines([new DatLineRecord
        {
            Id               = DatLineId,
            HardwareFamilyId = HwFamilyId,
            Name             = SystemName,
            Authority        = "no-intro",
            MediaTypeId      = "rom",
            DataStorePath    = dbRelPath,
            ImportedAtUtc    = DateTime.UtcNow,
        }]);
    }

    private DatLineStore OpenStore() =>
        new(Path.Combine(_dataDir, "systems", HwFamilyId, $"{DatLineId}.db"));

    private AmpExportPlanService Svc() => new(_dataDir, _catalog);

    private static ReleaseRecord MakeRelease(string id, string name) => new()
    {
        Id        = id,
        DatLineId = DatLineId,
        Name      = name,
        Status    = "present",
    };

    private string PlaceFile(string folder, string filename, byte[]? content = null)
    {
        var dir  = Path.Combine(_dataDir, "media", HwFamilyId, DatLineId, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, filename);
        File.WriteAllBytes(path, content ?? [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    // ── 1. Empty scope ─────────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_NoReleases_ReturnsEmptyPlan()
    {
        OpenStore(); // creates schema

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);

        Assert.Equal(0, plan.ReleaseCount);
        Assert.Equal(0, plan.ReleasesWithMetadata);
        Assert.Equal(0, plan.ReleasesWithMedia);
        Assert.Equal(0, plan.TotalMediaFiles);
        Assert.Equal(0L, plan.TotalBytes);
        Assert.Empty(plan.Releases);
        Assert.Empty(plan.Issues);
    }

    // ── 2. Complete metadata + media ───────────────────────────────────────────

    [Fact]
    public void PlanExport_FullRelease_ReturnsCorrectTotals()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);
        store.SaveReleaseMetadata(new ReleaseMetadataRecord
            { ReleaseId = ReleaseId1, Title = "Super Mario World" });

        var coverPath = PlaceFile("covers-front", "smw_wor_0.png");
        var sha256    = ReleaseMediaCurationService.ComputeSha256(coverPath)!;
        var fi        = new FileInfo(coverPath);
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId1,
            MediaType:      "cover-front",
            FilePath:       coverPath,
            FileSha256:     sha256,
            IsPreferred:    true,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);

        Assert.Equal(1, plan.ReleaseCount);
        Assert.Equal(1, plan.ReleasesWithMetadata);
        Assert.Equal(1, plan.ReleasesWithMedia);
        Assert.Equal(1, plan.TotalMediaFiles);
        Assert.Equal(fi.Length, plan.TotalBytes);

        var rel = Assert.Single(plan.Releases);
        Assert.True(rel.HasMetadata);
        Assert.Equal("Super Mario World", rel.Title);
        var entry = Assert.Single(rel.MediaEntries);
        Assert.Equal("cover-front", entry.MediaType);
        Assert.Equal(sha256, entry.Sha256);
        Assert.True(entry.IsPreferred);
    }

    // ── 3. Missing title ───────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_MetadataRowWithEmptyTitle_EmitsWarning()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);
        store.SaveReleaseMetadata(new ReleaseMetadataRecord
            { ReleaseId = ReleaseId1, Title = "" });

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);
        var rel  = Assert.Single(plan.Releases);

        Assert.False(rel.HasMetadata);
        Assert.Contains(rel.Issues, i =>
            i.Severity == AmpExportPlanSeverity.Warning &&
            i.Area == "metadata");
    }

    // ── 4. Missing cover-front ─────────────────────────────────────────────────

    [Fact]
    public void PlanExport_MetadataWithoutCoverFront_EmitsWarning()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);
        store.SaveReleaseMetadata(new ReleaseMetadataRecord
            { ReleaseId = ReleaseId1, Title = "Super Mario World" });
        // No curation rows → HasMetadata=true, no cover-front → Warning

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);
        var rel  = Assert.Single(plan.Releases);

        Assert.Contains(rel.Issues, i =>
            i.Severity == AmpExportPlanSeverity.Warning &&
            i.Area == "media" &&
            i.Message.Contains("no front cover"));
    }

    // ── 5. Missing file on disk ────────────────────────────────────────────────

    [Fact]
    public void PlanExport_MissingFile_EmitsWarningAndSkips()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId1,
            MediaType:      "cover-front",
            FilePath:       Path.Combine(_dataDir, "nonexistent", "ghost.png"),
            FileSha256:     null,
            IsPreferred:    false,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);
        var rel  = Assert.Single(plan.Releases);

        Assert.Empty(rel.MediaEntries);
        Assert.Contains(rel.Issues, i =>
            i.Severity == AmpExportPlanSeverity.Warning &&
            i.Area == "media" &&
            i.Message.Contains("not found"));
    }

    // ── 6. Zero-byte file ─────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_ZeroByteFile_EmitsErrorAndSkips()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);

        var path = PlaceFile("covers-front", "empty.png", []);
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId1,
            MediaType:      "cover-front",
            FilePath:       path,
            FileSha256:     null,
            IsPreferred:    false,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);
        var rel  = Assert.Single(plan.Releases);

        Assert.Empty(rel.MediaEntries);
        Assert.Contains(rel.Issues, i =>
            i.Severity == AmpExportPlanSeverity.Error &&
            i.Area == "media" &&
            i.Message.Contains("Zero-byte"));
    }

    // ── 7. SHA-256 mismatch ────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_Sha256Mismatch_EmitsErrorAndSkips()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);

        var path = PlaceFile("covers-front", "smw_wor_0.png");
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId1,
            MediaType:      "cover-front",
            FilePath:       path,
            FileSha256:     "0000000000000000000000000000000000000000000000000000000000000000",
            IsPreferred:    false,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);
        var rel  = Assert.Single(plan.Releases);

        Assert.Empty(rel.MediaEntries);
        Assert.Contains(rel.Issues, i =>
            i.Severity == AmpExportPlanSeverity.Error &&
            i.Area == "media" &&
            i.Message.Contains("mismatch"));
    }

    // ── 8. Preferred media ────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_PreferredMedia_PropagatesIsPreferred()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);

        var path   = PlaceFile("covers-front", "smw_wor_0.png");
        var sha256 = ReleaseMediaCurationService.ComputeSha256(path)!;
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId1,
            MediaType:      "cover-front",
            FilePath:       path,
            FileSha256:     sha256,
            IsPreferred:    true,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        var plan  = Svc().PlanExport(HwFamilyId, DatLineId);
        var entry = Assert.Single(plan.Releases[0].MediaEntries);

        Assert.True(entry.IsPreferred);
    }

    // ── 9. Credits ────────────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_Credits_PropagatedToEntry()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);

        var path   = PlaceFile("covers-front", "smw_wor_0.png");
        var sha256 = ReleaseMediaCurationService.ComputeSha256(path)!;
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId1,
            MediaType:      "cover-front",
            FilePath:       path,
            FileSha256:     sha256,
            IsPreferred:    false,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        "Scan by user123",
            Notes:          null));

        var plan  = Svc().PlanExport(HwFamilyId, DatLineId);
        var entry = Assert.Single(plan.Releases[0].MediaEntries);

        Assert.Equal("Scan by user123", entry.Credits);
    }

    // ── 10. Extra notes ───────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_ExtraNotes_IncludedInRelease()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);
        store.SaveReleaseExtraNotes(ReleaseId1, "My curator note.");

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);
        var rel  = Assert.Single(plan.Releases);

        Assert.Equal("My curator note.", rel.ExtraNotes);
        Assert.Equal(1, plan.ExtraNotesCount);
    }

    // ── 11. Excluded media ────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_ExcludedMedia_InHashesNotInEntries()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);

        const string fakeHash =
            "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId1,
            MediaType:      "cover-front",
            FilePath:       Path.Combine(_dataDir, "excluded.png"),
            FileSha256:     fakeHash,
            IsPreferred:    false,
            IsExcluded:     true,
            ExcludedReason: "wrong art",
            Credits:        null,
            Notes:          null));

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);
        var rel  = Assert.Single(plan.Releases);

        Assert.Empty(rel.MediaEntries);
        Assert.Contains(fakeHash, rel.ExclusionHashes);
        Assert.Equal(1, plan.ExclusionCount);
    }

    // ── 12. Deleted row does not appear ───────────────────────────────────────

    [Fact]
    public void PlanExport_NoCurationRow_NoEntryInPlan()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);
        // No curation row — simulates Delete File (row removed from DB)

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);
        var rel  = Assert.Single(plan.Releases);

        Assert.Empty(rel.MediaEntries);
        Assert.Empty(rel.ExclusionHashes);
    }

    // ── 13. Duplicate hash warning ────────────────────────────────────────────

    [Fact]
    public void PlanExport_DuplicateHash_EmitsWarning()
    {
        var store = OpenStore();
        store.SaveReleases([
            MakeRelease(ReleaseId1, RelName1),
            MakeRelease(ReleaseId2, RelName2),
        ]);

        // Same file content → same SHA-256, but different filenames and releases
        var content = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var path1   = PlaceFile("covers-front", "smw_wor_0.png", content);
        var path2   = PlaceFile("covers-front", "dkc_wor_0.png", content);
        var sha256  = ReleaseMediaCurationService.ComputeSha256(path1)!;

        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId1, "cover-front", path1, sha256, false, false, null, null, null));
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId2, "cover-front", path2, sha256, false, false, null, null, null));

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);

        // Whichever release is processed second (alphabetical order) gets the warning.
        Assert.Contains(plan.Releases, r => r.Issues.Any(i =>
            i.Severity == AmpExportPlanSeverity.Warning &&
            i.Area == "dedup"));
    }

    // ── 14. Duplicate archive path error ──────────────────────────────────────

    [Fact]
    public void PlanExport_DuplicateArchivePath_EmitsErrorAndSkipsSecond()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);

        // Two files with the same filename in different directories, same release+mediaType.
        // Both map to media/cover-front/rel-001/cover.png → archive path collision within the release.
        var path1 = PlaceFile("covers-front-a", "cover.png", [0x01, 0x02, 0x03]);
        var path2 = PlaceFile("covers-front-b", "cover.png", [0x04, 0x05, 0x06]);
        var sha1  = ReleaseMediaCurationService.ComputeSha256(path1)!;
        var sha2  = ReleaseMediaCurationService.ComputeSha256(path2)!;

        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId1, "cover-front", path1, sha1, false, false, null, null, null));
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId1, "cover-front", path2, sha2, false, false, null, null, null));

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);
        var rel  = Assert.Single(plan.Releases);

        Assert.Contains(rel.Issues, i =>
            i.Severity == AmpExportPlanSeverity.Error &&
            i.Area == "archive");
        Assert.Single(rel.MediaEntries); // second file skipped
    }

    // ── 15. No provider / provenance fields in plan ───────────────────────────

    [Fact]
    public void PlanExport_DoesNotExposeProviderPayloadOrScrapedAt()
    {
        var store = OpenStore();
        store.SaveReleases([MakeRelease(ReleaseId1, RelName1)]);
        store.SaveReleaseMetadata(new ReleaseMetadataRecord
        {
            ReleaseId    = ReleaseId1,
            Title        = "Super Mario World",
            ScrapedAtUtc = "2026-01-01T00:00:00Z",
        });
        store.SaveProviderPayload(ReleaseId1, "screenscraper", "{\"ssuser\":{\"id\":\"42\"}}");

        var plan = Svc().PlanExport(HwFamilyId, DatLineId);
        var rel  = Assert.Single(plan.Releases);

        Assert.Equal("Super Mario World", rel.Title);

        // Verify by reflection that the plan types have no provider-provenance properties
        var releaseType = typeof(AmpExportPlanRelease);
        var entryType   = typeof(AmpExportPlanMediaEntry);
        var planType    = typeof(AmpExportPlan);
        Assert.Null(releaseType.GetProperty("ScrapedAtUtc"));
        Assert.Null(releaseType.GetProperty("ProviderPayload"));
        Assert.Null(releaseType.GetProperty("Provider"));
        Assert.Null(entryType.GetProperty("Provider"));
        Assert.Null(planType.GetProperty("ScrapedAtUtc"));
    }
}
