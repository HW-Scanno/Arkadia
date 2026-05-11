using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Amp;

public sealed class AmpExportWriterServiceTests : IDisposable
{
    private readonly string               _baseDir;
    private readonly AmpExportWriterService _svc;

    public AmpExportWriterServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
        _svc = new AmpExportWriterService();
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string TempAmpPath() =>
        Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".amp");

    private string PlaceMediaFile(string name, byte[]? content = null)
    {
        var path = Path.Combine(_baseDir, "media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    private static AmpExportPlanMediaEntry MediaEntry(
        string mediaType, string filePath, string? sha256 = null, bool isPreferred = false) =>
        new(
            MediaType:   mediaType,
            FilePath:    filePath,
            Sha256:      sha256 ?? ReleaseMediaCurationService.ComputeSha256(filePath)!,
            SizeBytes:   new FileInfo(filePath).Length,
            IsPreferred: isPreferred,
            Credits:     null);

    private static AmpExportPlanRelease ReleaseWith(
        string                                 id,
        string                                 datName,
        IReadOnlyList<AmpExportPlanMediaEntry>? media           = null,
        IReadOnlyList<string>?                  exclusionHashes = null,
        string?                                extraNotes      = null,
        IReadOnlyList<AmpExportPlanIssue>?      issues          = null) =>
        new(
            ReleaseId:       id,
            DatName:         datName,
            Title:           datName,
            OriginalTitle:   "",
            SortTitle:       "",
            Developer:       "Dev Co",
            Publisher:       "Pub Co",
            Year:            "1992",
            Languages:       "en",
            AlternateTitles: "",
            Description:     "",
            Genre:           "",
            Subgenre:        "",
            Players:         "",
            ReleaseType:     "",
            Rating:          "",
            HasMetadata:     true,
            MediaEntries:    media           ?? [],
            ExclusionHashes: exclusionHashes ?? [],
            ExtraNotes:      extraNotes,
            Issues:          issues          ?? []);

    private static AmpExportPlan PlanWith(
        IReadOnlyList<AmpExportPlanRelease> releases,
        IReadOnlyList<AmpExportPlanIssue>?  issues = null) =>
        new(
            HardwareFamilyId:     "snes",
            DatLineId:            "snes-nointro",
            SystemName:           "Super Nintendo",
            ReleaseCount:         releases.Count,
            ReleasesWithMetadata: releases.Count(r => r.HasMetadata),
            ReleasesWithMedia:    releases.Count(r => r.MediaEntries.Count > 0),
            TotalMediaFiles:      releases.Sum(r => r.MediaEntries.Count),
            TotalBytes:           releases.Sum(r => r.MediaEntries.Sum(e => e.SizeBytes)),
            ExclusionCount:       releases.Sum(r => r.ExclusionHashes.Count),
            ExtraNotesCount:      releases.Count(r => r.ExtraNotes?.Length > 0),
            Releases:             releases,
            Issues:               issues ?? []);

    private static string ReadZipEntryText(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName)
            ?? throw new InvalidOperationException($"ZIP entry '{entryName}' not found.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ── 1. Creates .amp file ──────────────────────────────────────────────────

    [Fact]
    public void Write_ValidPlan_CreatesAmpFile()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var release  = ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)]);
        var plan    = PlanWith([release]);
        var outPath = TempAmpPath();

        var result = _svc.Write(plan, outPath);

        Assert.True(result.Success);
        Assert.True(File.Exists(outPath));
        Assert.Equal(outPath, result.OutputPath);
        Assert.True(result.PackageBytes > 0);
    }

    // ── 2. Contains manifest.json ─────────────────────────────────────────────

    [Fact]
    public void Write_ValidPlan_ContainsManifestJson()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath = TempAmpPath();

        _svc.Write(plan, outPath);

        using var zip = ZipFile.OpenRead(outPath);
        Assert.NotNull(zip.GetEntry("manifest.json"));
        var json = ReadZipEntryText(zip, "manifest.json");
        Assert.Contains("Arkadia Media Pack", json);
        Assert.Contains("snes-nointro", json);
    }

    // ── 3. Contains releases.json ─────────────────────────────────────────────

    [Fact]
    public void Write_ValidPlan_ContainsReleasesJson()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath = TempAmpPath();

        _svc.Write(plan, outPath);

        using var zip = ZipFile.OpenRead(outPath);
        Assert.NotNull(zip.GetEntry("releases.json"));
        var json = ReadZipEntryText(zip, "releases.json");
        Assert.Contains("rel-001", json);
        Assert.Contains("Dev Co", json);
    }

    // ── 4. Contains media file ────────────────────────────────────────────────

    [Fact]
    public void Write_ValidPlan_ContainsMediaFile()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath = TempAmpPath();

        _svc.Write(plan, outPath);

        using var zip  = ZipFile.OpenRead(outPath);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("media/cover-front/rel-001/smw_cover.png", names);
    }

    // ── 5. Contains exclusions.json ───────────────────────────────────────────

    [Fact]
    public void Write_ValidPlan_ContainsExclusionsJson()
    {
        const string hash     = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media:           [MediaEntry("cover-front", filePath)],
            exclusionHashes: [hash])]);
        var outPath = TempAmpPath();

        _svc.Write(plan, outPath);

        using var zip = ZipFile.OpenRead(outPath);
        Assert.NotNull(zip.GetEntry("curation/exclusions.json"));
        var json = ReadZipEntryText(zip, "curation/exclusions.json");
        Assert.Contains(hash, json);
    }

    // ── 6. Contains notes.json ────────────────────────────────────────────────

    [Fact]
    public void Write_ValidPlan_ContainsNotesJson()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media:      [MediaEntry("cover-front", filePath)],
            extraNotes: "Curator note for SMW.")]);
        var outPath = TempAmpPath();

        _svc.Write(plan, outPath);

        using var zip = ZipFile.OpenRead(outPath);
        Assert.NotNull(zip.GetEntry("curation/notes.json"));
        var json = ReadZipEntryText(zip, "curation/notes.json");
        Assert.Contains("Curator note for SMW.", json);
    }

    // ── 7. Contains files.sha256.json ─────────────────────────────────────────

    [Fact]
    public void Write_ValidPlan_ContainsFilesSha256Json()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath = TempAmpPath();

        _svc.Write(plan, outPath);

        using var zip = ZipFile.OpenRead(outPath);
        Assert.NotNull(zip.GetEntry("hashes/files.sha256.json"));
        var json = ReadZipEntryText(zip, "hashes/files.sha256.json");
        Assert.Contains("manifest.json", json);
        Assert.Contains("releases.json", json);
    }

    // ── 8. Plan with errors — refuses ────────────────────────────────────────

    [Fact]
    public void Write_PlanWithErrorIssue_RefusesToWrite()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith(
            [ReleaseWith("rel-001", "SMW", media: [MediaEntry("cover-front", filePath)])],
            issues: [new AmpExportPlanIssue(AmpExportPlanSeverity.Error, "media", "bad file")]);
        var outPath = TempAmpPath();

        Assert.Throws<InvalidOperationException>(() => _svc.Write(plan, outPath));
        Assert.False(File.Exists(outPath));
    }

    // ── 9. Overwrite=false with existing output — refuses ────────────────────

    [Fact]
    public void Write_OutputExistsOverwriteFalse_RefusesToWrite()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath = TempAmpPath();
        File.WriteAllBytes(outPath, [0x00]); // pre-existing file

        Assert.Throws<InvalidOperationException>(() => _svc.Write(plan, outPath, overwrite: false));
    }

    // ── 10. Overwrite=true — replaces existing file ───────────────────────────

    [Fact]
    public void Write_OutputExistsOverwriteTrue_ReplacesFile()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath = TempAmpPath();
        File.WriteAllBytes(outPath, [0x00]); // stale 1-byte file

        var result = _svc.Write(plan, outPath, overwrite: true);

        Assert.True(result.Success);
        Assert.True(new FileInfo(outPath).Length > 1);
    }

    // ── 11. Missing media file — cleans tmp, no output ────────────────────────

    [Fact]
    public void Write_MissingMediaFile_CleansTmpAndDoesNotCreateOutput()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var sha256   = ReleaseMediaCurationService.ComputeSha256(filePath)!;
        var entry    = MediaEntry("cover-front", filePath, sha256);
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW", media: [entry])]);
        var outPath  = TempAmpPath();

        File.Delete(filePath); // file disappears after planning

        Assert.Throws<InvalidOperationException>(() => _svc.Write(plan, outPath));
        Assert.False(File.Exists(outPath));
        Assert.False(File.Exists(outPath + ".tmp"));
    }

    // ── 12. Zero-byte media file — refuses ────────────────────────────────────

    [Fact]
    public void Write_ZeroByteMediaFile_RefusesToWrite()
    {
        var filePath = PlaceMediaFile("empty.png", []);
        var sha256   = ReleaseMediaCurationService.ComputeSha256(filePath)!;
        var entry    = new AmpExportPlanMediaEntry("cover-front", filePath, sha256, 0, false, null);
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW", media: [entry])]);

        Assert.Throws<InvalidOperationException>(() => _svc.Write(plan, TempAmpPath()));
    }

    // ── 13. SHA-256 mismatch — refuses ───────────────────────────────────────

    [Fact]
    public void Write_Sha256Mismatch_RefusesToWrite()
    {
        var filePath = PlaceMediaFile("smw_cover.png", [0x01, 0x02, 0x03]);
        const string wrongSha = "0000000000000000000000000000000000000000000000000000000000000000";
        var entry = new AmpExportPlanMediaEntry("cover-front", filePath, wrongSha, 3, false, null);
        var plan  = PlanWith([ReleaseWith("rel-001", "SMW", media: [entry])]);

        Assert.Throws<InvalidOperationException>(() => _svc.Write(plan, TempAmpPath()));
    }

    // ── 14. Uses per-release archive path — no collision ─────────────────────

    [Fact]
    public void Write_UsesSafePerReleaseMediaPath()
    {
        var file1 = PlaceMediaFile("a/cover.png", [0x01, 0x02, 0x03, 0x04]);
        var file2 = PlaceMediaFile("b/cover.png", [0x05, 0x06, 0x07, 0x08]);
        var rel1  = ReleaseWith("rel-001", "Game A", media: [MediaEntry("cover-front", file1)]);
        var rel2  = ReleaseWith("rel-002", "Game B", media: [MediaEntry("cover-front", file2)]);
        var plan  = PlanWith([rel1, rel2]);
        var outPath = TempAmpPath();

        _svc.Write(plan, outPath);

        using var zip   = ZipFile.OpenRead(outPath);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();

        // Same filename, same mediaType but different releases → no collision
        Assert.Contains("media/cover-front/rel-001/cover.png", names);
        Assert.Contains("media/cover-front/rel-002/cover.png", names);
    }

    // ── 15. releases.json has no local absolute path ──────────────────────────

    [Fact]
    public void Write_DoesNotIncludeLocalAbsoluteFilePathInJson()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath = TempAmpPath();

        _svc.Write(plan, outPath);

        using var zip  = ZipFile.OpenRead(outPath);
        var json = ReadZipEntryText(zip, "releases.json");

        Assert.DoesNotContain(filePath,                             json);
        Assert.DoesNotContain(filePath.Replace('\\', '/'), json);
    }

    // ── 16. No provider / provenance fields in any JSON ──────────────────────

    [Fact]
    public void Write_DoesNotIncludeProviderProvenanceFields()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath = TempAmpPath();

        _svc.Write(plan, outPath);

        using var zip = ZipFile.OpenRead(outPath);
        var allJson = ReadZipEntryText(zip, "manifest.json")
                    + ReadZipEntryText(zip, "releases.json");

        Assert.DoesNotContain("ScrapedAtUtc",    allJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProviderPayload", allJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ssuser",          allJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Provider\"",    allJson, StringComparison.OrdinalIgnoreCase);
    }

    // ── 17. Returns correct package SHA-256 ──────────────────────────────────

    [Fact]
    public void Write_ReturnsPackageSha256()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath = TempAmpPath();

        var result = _svc.Write(plan, outPath);

        var expected = ReleaseMediaCurationService.ComputeSha256(outPath)!;
        Assert.Equal(expected, result.Sha256);
    }

    // ── 18. Zero releases — refuses ───────────────────────────────────────────

    [Fact]
    public void Write_ZeroReleases_RefusesToWrite()
    {
        var plan = PlanWith([]);
        Assert.Throws<InvalidOperationException>(() => _svc.Write(plan, TempAmpPath()));
    }

    // ── 19. manifest.json contains default Attribution ────────────────────────

    [Fact]
    public void Write_ValidPlan_ManifestContainsDefaultAttribution()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath  = TempAmpPath();

        _svc.Write(plan, outPath);

        using var zip  = ZipFile.OpenRead(outPath);
        var json       = ReadZipEntryText(zip, "manifest.json");
        using var doc  = JsonDocument.Parse(json);
        var root       = doc.RootElement;

        Assert.True(root.TryGetProperty("Attribution", out var attr));
        Assert.Equal(JsonValueKind.Object, attr.ValueKind);

        Assert.True(attr.TryGetProperty("Notice", out var notice));
        Assert.Equal(AmpAttribution.DefaultNotice, notice.GetString());

        Assert.True(attr.TryGetProperty("GeneralCredits", out var credits));
        Assert.Equal(AmpAttribution.DefaultGeneralCredits, credits.GetString());
    }
}
