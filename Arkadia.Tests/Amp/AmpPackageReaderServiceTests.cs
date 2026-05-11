using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Amp;

public sealed class AmpPackageReaderServiceTests : IDisposable
{
    private readonly string                  _baseDir;
    private readonly AmpExportWriterService  _writer;
    private readonly AmpPackageReaderService _reader;

    public AmpPackageReaderServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
        _writer = new AmpExportWriterService();
        _reader = new AmpPackageReaderService();
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string PlaceMediaFile(string name)
    {
        var path = Path.Combine(_baseDir, "media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    private static AmpExportPlanMediaEntry MediaEntry(string mediaType, string filePath) =>
        new(MediaType:   mediaType,
            FilePath:    filePath,
            Sha256:      ReleaseMediaCurationService.ComputeSha256(filePath)!,
            SizeBytes:   new FileInfo(filePath).Length,
            IsPreferred: true,
            Credits:     "Test Author");

    private static AmpExportPlanRelease ReleaseWith(
        string id, string datName,
        IReadOnlyList<AmpExportPlanMediaEntry>? media      = null,
        IReadOnlyList<string>?                  exclusions = null) =>
        new(ReleaseId:       id,
            DatName:         datName,
            Title:           datName + " Title",
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
            MediaEntries:    media ?? [],
            ExclusionHashes: exclusions ?? [],
            ExtraNotes:      null,
            Issues:          []);

    private static AmpExportPlan PlanWith(
        IReadOnlyList<AmpExportPlanRelease> releases,
        string hwFamily      = "snes",
        string datLine       = "snes-nointro",
        int    exclusionCount = 0) =>
        new(HardwareFamilyId:     hwFamily,
            DatLineId:            datLine,
            SystemName:           "Super Nintendo",
            ReleaseCount:         releases.Count,
            ReleasesWithMetadata: releases.Count,
            ReleasesWithMedia:    releases.Count,
            TotalMediaFiles:      0,
            TotalBytes:           0L,
            ExclusionCount:       exclusionCount,
            ExtraNotesCount:      0,
            Releases:             releases,
            Issues:               []);

    private string CreateAmp(
        IReadOnlyList<AmpExportPlanRelease>? releases     = null,
        string                               hwFamily      = "snes",
        string                               datLine       = "snes-nointro",
        int                                  exclusionCount = 0)
    {
        var r    = releases ?? [ReleaseWith("rel-001", "SMW")];
        var plan = PlanWith(r, hwFamily, datLine, exclusionCount);
        var path = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".amp");
        _writer.Write(plan, path);
        return path;
    }

    private string CreateAmpWithoutEntry(string entryToRemove)
    {
        var src = CreateAmp();
        var dst = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".amp");

        using (var srcZip = ZipFile.OpenRead(src))
        using (var dstFs  = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var dstZip = new ZipArchive(dstFs, ZipArchiveMode.Create))
        {
            foreach (var entry in srcZip.Entries)
            {
                if (string.Equals(entry.FullName, entryToRemove, StringComparison.Ordinal)) continue;
                var newEntry = dstZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var src2 = entry.Open();
                using var dst2 = newEntry.Open();
                src2.CopyTo(dst2);
            }
        }

        return dst;
    }

    private string CreateAmpWithCorruptJson(string entryToCorrupt)
    {
        var src = CreateAmp();
        var dst = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".amp");

        var garbage = Encoding.UTF8.GetBytes("{ NOT VALID JSON !!!");

        using (var srcZip = ZipFile.OpenRead(src))
        using (var dstFs  = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var dstZip = new ZipArchive(dstFs, ZipArchiveMode.Create))
        {
            foreach (var entry in srcZip.Entries)
            {
                var newEntry = dstZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var dst2 = newEntry.Open();
                if (string.Equals(entry.FullName, entryToCorrupt, StringComparison.Ordinal))
                {
                    dst2.Write(garbage, 0, garbage.Length);
                }
                else
                {
                    using var src2 = entry.Open();
                    src2.CopyTo(dst2);
                }
            }
        }

        return dst;
    }

    // ── 1. TryReadReleases — valid AMP returns all releases ───────────────────

    [Fact]
    public void TryReadReleases_ValidAmp_ReturnsAllReleases()
    {
        var amp = CreateAmp(releases:
        [
            ReleaseWith("rel-001", "Game A"),
            ReleaseWith("rel-002", "Game B"),
        ]);

        var ok = _reader.TryReadReleases(amp, out var releases);

        Assert.True(ok);
        Assert.Equal(2, releases.Count);
        Assert.Contains(releases, r => r.ReleaseId == "rel-001" && r.DatName == "Game A");
        Assert.Contains(releases, r => r.ReleaseId == "rel-002" && r.DatName == "Game B");
    }

    // ── 2. TryReadReleases — media entries populated ──────────────────────────

    [Fact]
    public void TryReadReleases_ValidAmp_MediaEntriesPopulated()
    {
        var mediaFile = PlaceMediaFile("cover.png");
        var entry     = MediaEntry("cover-front", mediaFile);
        var amp       = CreateAmp(releases: [ReleaseWith("rel-001", "Game A", media: [entry])]);

        var ok = _reader.TryReadReleases(amp, out var releases);

        Assert.True(ok);
        Assert.Single(releases);
        var media = releases[0].Media;
        Assert.Single(media);
        Assert.Equal("cover-front",        media[0].MediaType);
        Assert.Equal("cover.png",          media[0].FileName);
        Assert.Equal(entry.Sha256,         media[0].Sha256);
        Assert.Equal(entry.SizeBytes,      media[0].SizeBytes);
        Assert.True(media[0].Preferred);
        Assert.Equal("Test Author",        media[0].Credits);
        Assert.StartsWith("media/cover-front/rel-001/", media[0].ArchivePath);
    }

    // ── 3. TryReadReleases — missing releases.json returns false ─────────────

    [Fact]
    public void TryReadReleases_MissingReleasesJson_ReturnsFalse()
    {
        var amp = CreateAmpWithoutEntry("releases.json");

        var ok = _reader.TryReadReleases(amp, out var releases);

        Assert.False(ok);
        Assert.Empty(releases);
    }

    // ── 4. TryReadReleases — invalid JSON returns false ───────────────────────

    [Fact]
    public void TryReadReleases_InvalidJson_ReturnsFalse()
    {
        var amp = CreateAmpWithCorruptJson("releases.json");

        var ok = _reader.TryReadReleases(amp, out var releases);

        Assert.False(ok);
        Assert.Empty(releases);
    }

    // ── 5. TryReadExclusions — valid AMP returns exclusions ───────────────────

    [Fact]
    public void TryReadExclusions_ValidAmp_ReturnsExclusions()
    {
        const string hash = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";
        var amp = CreateAmp(
            releases:      [ReleaseWith("rel-001", "Game A", exclusions: [hash])],
            exclusionCount: 1);

        var ok = _reader.TryReadExclusions(amp, out var exclusions);

        Assert.True(ok);
        Assert.Single(exclusions);
        Assert.Equal("rel-001", exclusions[0].ReleaseId);
        Assert.Equal("Game A",  exclusions[0].DatName);
        Assert.Equal(hash,      exclusions[0].Sha256);
    }

    // ── 6. TryReadExclusions — missing entry returns true + empty list ────────

    [Fact]
    public void TryReadExclusions_MissingEntry_ReturnsEmpty()
    {
        var amp = CreateAmpWithoutEntry("curation/exclusions.json");

        var ok = _reader.TryReadExclusions(amp, out var exclusions);

        Assert.True(ok);
        Assert.Empty(exclusions);
    }

    // ── 7. OpenMediaStream — valid entry stream is readable ───────────────────

    [Fact]
    public void OpenMediaStream_ValidEntry_StreamReadable()
    {
        var mediaFile = PlaceMediaFile("cover.png");
        var entry     = MediaEntry("cover-front", mediaFile);
        var amp       = CreateAmp(releases: [ReleaseWith("rel-001", "Game A", media: [entry])]);

        _reader.TryReadReleases(amp, out var releases);
        var archivePath = releases[0].Media[0].ArchivePath;

        using var stream = _reader.OpenMediaStream(amp, archivePath);

        var buf  = new byte[64];
        var read = stream.Read(buf, 0, buf.Length);
        Assert.True(read > 0);
        Assert.Equal(0x89, buf[0]); // PNG magic
    }

    // ── 8. OpenMediaStream — missing entry throws ─────────────────────────────

    [Fact]
    public void OpenMediaStream_MissingEntry_Throws()
    {
        var amp = CreateAmp();

        Assert.Throws<FileNotFoundException>(() =>
            _reader.OpenMediaStream(amp, "media/cover-front/rel-001/nonexistent.png"));
    }

    // ── 9. ListPackagesForScope — matching scope returns package ──────────────

    [Fact]
    public void ListPackagesForScope_MatchingScope_ReturnsPackage()
    {
        var registry = new AmpLocalRegistryService(_baseDir);
        registry.EnsureFolder();

        var outPath = Path.Combine(registry.RegistryFolder, "test.amp");
        _writer.Write(PlanWith([ReleaseWith("rel-001", "Game A")]), outPath);

        var result = registry.ListPackagesForScope("snes", "snes-nointro");

        Assert.Single(result);
        Assert.Equal("snes",         result[0].HardwareFamilyId);
        Assert.Equal("snes-nointro", result[0].DatLineId);
    }

    // ── 10. ListPackagesForScope — non-matching scope returns empty ───────────

    [Fact]
    public void ListPackagesForScope_NonMatchingScope_ReturnsEmpty()
    {
        var registry = new AmpLocalRegistryService(_baseDir);
        registry.EnsureFolder();

        var outPath = Path.Combine(registry.RegistryFolder, "test.amp");
        _writer.Write(PlanWith([ReleaseWith("rel-001", "Game A")]), outPath);

        var result = registry.ListPackagesForScope("nes", "nes-nointro");

        Assert.Empty(result);
    }

    // ── 11. FindRelease — exact ReleaseId match ───────────────────────────────

    [Fact]
    public void FindRelease_ExactReleaseId_ReturnsReleaseIdMatch()
    {
        var releases = new List<AmpReleaseInfo>
        {
            new("rel-001", "Game A", "Game A Title", "", "", "", "", "", "", "", "", "", "", "", "", "", []),
            new("rel-002", "Game B", "Game B Title", "", "", "", "", "", "", "", "", "", "", "", "", "", []),
        };

        var result = AmpReleaseMatcher.FindRelease(releases, "rel-001", "no-match");

        Assert.Equal(AmpReleaseMatchKind.ReleaseId, result.Kind);
        Assert.NotNull(result.Release);
        Assert.Equal("rel-001", result.Release.ReleaseId);
    }

    // ── 12. FindRelease — DatName fallback ────────────────────────────────────

    [Fact]
    public void FindRelease_DatNameFallback_ReturnsDatNameMatch()
    {
        var releases = new List<AmpReleaseInfo>
        {
            new("rel-001", "Game A", "Game A Title", "", "", "", "", "", "", "", "", "", "", "", "", "", []),
        };

        var result = AmpReleaseMatcher.FindRelease(releases, "no-such-id", "Game A");

        Assert.Equal(AmpReleaseMatchKind.DatName, result.Kind);
        Assert.NotNull(result.Release);
        Assert.Equal("rel-001", result.Release.ReleaseId);
    }

    // ── 13. FindRelease — no match returns None ───────────────────────────────

    [Fact]
    public void FindRelease_NoMatch_ReturnsNone()
    {
        var releases = new List<AmpReleaseInfo>
        {
            new("rel-001", "Game A", "Game A Title", "", "", "", "", "", "", "", "", "", "", "", "", "", []),
        };

        var result = AmpReleaseMatcher.FindRelease(releases, "no-id", "no-name");

        Assert.Equal(AmpReleaseMatchKind.None, result.Kind);
        Assert.Null(result.Release);
    }

    // ── 14. FindRelease — Title match alone does not count ────────────────────

    [Fact]
    public void FindRelease_DoesNotUseTitleFallback()
    {
        var releases = new List<AmpReleaseInfo>
        {
            new("rel-001", "dat-name-a", "Shared Title", "", "", "", "", "", "", "", "", "", "", "", "", "", []),
        };

        // Both releaseId and datName do not match; only Title matches.
        var result = AmpReleaseMatcher.FindRelease(releases, "no-id", "no-name");

        Assert.Equal(AmpReleaseMatchKind.None, result.Kind);
        Assert.Null(result.Release);
    }
}
