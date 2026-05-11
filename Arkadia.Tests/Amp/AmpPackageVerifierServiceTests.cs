using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Amp;

public sealed class AmpPackageVerifierServiceTests : IDisposable
{
    private readonly string                   _baseDir;
    private readonly AmpExportWriterService   _writer;
    private readonly AmpPackageVerifierService _svc;

    public AmpPackageVerifierServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
        _writer = new AmpExportWriterService();
        _svc    = new AmpPackageVerifierService();
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // ── Plan/writer helpers ───────────────────────────────────────────────────

    private string PlaceMediaFile(string name, byte[]? content = null)
    {
        var path = Path.Combine(_baseDir, "media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    private static AmpExportPlanMediaEntry MediaEntry(string mediaType, string filePath) =>
        new(
            MediaType:   mediaType,
            FilePath:    filePath,
            Sha256:      ReleaseMediaCurationService.ComputeSha256(filePath)!,
            SizeBytes:   new FileInfo(filePath).Length,
            IsPreferred: false,
            Credits:     null);

    private static AmpExportPlanRelease ReleaseWith(
        string id, string datName,
        IReadOnlyList<AmpExportPlanMediaEntry>? media = null) =>
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
            MediaEntries:    media ?? [],
            ExclusionHashes: [],
            ExtraNotes:      null,
            Issues:          []);

    private static AmpExportPlan PlanWith(IReadOnlyList<AmpExportPlanRelease> releases) =>
        new(
            HardwareFamilyId:     "snes",
            DatLineId:            "snes-nointro",
            SystemName:           "Super Nintendo",
            ReleaseCount:         releases.Count,
            ReleasesWithMetadata: releases.Count,
            ReleasesWithMedia:    releases.Count,
            TotalMediaFiles:      0,
            TotalBytes:           0L,
            ExclusionCount:       0,
            ExtraNotesCount:      0,
            Releases:             releases,
            Issues:               []);

    private string CreateValidAmpFile()
    {
        var filePath = PlaceMediaFile("smw_cover.png");
        var plan     = PlanWith([ReleaseWith("rel-001", "SMW",
            media: [MediaEntry("cover-front", filePath)])]);
        var outPath  = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".amp");
        _writer.Write(plan, outPath);
        return outPath;
    }

    // Creates a .amp file but removes a specific ZIP entry
    private string CreateAmpWithoutEntry(string entryToRemove)
    {
        var src = CreateValidAmpFile();
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

    // Creates a .amp file with one entry replaced by corrupt bytes
    private string CreateAmpWithCorruptEntry(string entryToCorrupt)
    {
        var src = CreateValidAmpFile();
        var dst = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".amp");

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
                    var garbage = Encoding.UTF8.GetBytes("{ NOT VALID JSON !!!");
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

    // Creates a .amp file with one JSON entry replaced by the given content
    private string CreateAmpWithReplacedJson(string entryName, string newJson)
    {
        var src = CreateValidAmpFile();
        var dst = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".amp");

        var newBytes = Encoding.UTF8.GetBytes(newJson);

        using (var srcZip = ZipFile.OpenRead(src))
        using (var dstFs  = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var dstZip = new ZipArchive(dstFs, ZipArchiveMode.Create))
        {
            foreach (var entry in srcZip.Entries)
            {
                var newEntry = dstZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var dst2 = newEntry.Open();
                if (string.Equals(entry.FullName, entryName, StringComparison.Ordinal))
                {
                    dst2.Write(newBytes, 0, newBytes.Length);
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

    // Creates a .amp file with an extra entry injected
    private string CreateAmpWithExtraEntry(string extraEntryName, byte[] content)
    {
        var src = CreateValidAmpFile();
        var dst = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".amp");

        using (var srcZip = ZipFile.OpenRead(src))
        using (var dstFs  = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var dstZip = new ZipArchive(dstFs, ZipArchiveMode.Create))
        {
            foreach (var entry in srcZip.Entries)
            {
                var newEntry = dstZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var src2 = entry.Open();
                using var dst2 = newEntry.Open();
                src2.CopyTo(dst2);
            }
            var extra = dstZip.CreateEntry(extraEntryName, CompressionLevel.Optimal);
            using var extStream = extra.Open();
            extStream.Write(content, 0, content.Length);
        }

        return dst;
    }

    // ── 1. File not found ─────────────────────────────────────────────────────

    [Fact]
    public void Verify_FileNotFound_ReturnsError()
    {
        var result = _svc.Verify(Path.Combine(_baseDir, "nonexistent.amp"));

        Assert.False(result.FileExists);
        Assert.False(result.ZipReadable);
        Assert.True(result.HasErrors);
        Assert.Equal("Error", result.Status);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "File");
    }

    // ── 2. Not a ZIP ──────────────────────────────────────────────────────────

    [Fact]
    public void Verify_NotAZip_ReturnsError()
    {
        var path = Path.Combine(_baseDir, "bad.amp");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03]);

        var result = _svc.Verify(path);

        Assert.True(result.FileExists);
        Assert.False(result.ZipReadable);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "File");
    }

    // ── 3. Valid package ──────────────────────────────────────────────────────

    [Fact]
    public void Verify_ValidPackage_ReturnsValid()
    {
        var amp = CreateValidAmpFile();

        var result = _svc.Verify(amp);

        Assert.True(result.FileExists);
        Assert.True(result.ZipReadable);
        Assert.True(result.ManifestPresent);
        Assert.True(result.ManifestValid);
        Assert.True(result.ReleasesPresent);
        Assert.True(result.ReleasesValid);
        Assert.True(result.HashFilePresent);
        Assert.True(result.HashFileValid);
        Assert.False(result.HasErrors);
        Assert.Equal("Valid", result.Status);
        Assert.Equal(1, result.ReleasesReleaseCount);
        Assert.Equal(1, result.MediaFilesFound);
        Assert.Equal(0, result.MediaFilesMissing);
        Assert.Equal(0, result.Sha256Mismatches);
    }

    // ── 4. Missing manifest.json ──────────────────────────────────────────────

    [Fact]
    public void Verify_MissingManifest_ReturnsError()
    {
        var amp    = CreateAmpWithoutEntry("manifest.json");
        var result = _svc.Verify(amp);

        Assert.False(result.ManifestPresent);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Manifest");
    }

    // ── 5. Missing releases.json ──────────────────────────────────────────────

    [Fact]
    public void Verify_MissingReleases_ReturnsError()
    {
        var amp    = CreateAmpWithoutEntry("releases.json");
        var result = _svc.Verify(amp);

        Assert.False(result.ReleasesPresent);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Releases");
    }

    // ── 6. Missing hashes/files.sha256.json ───────────────────────────────────

    [Fact]
    public void Verify_MissingHashFile_ReturnsError()
    {
        var amp    = CreateAmpWithoutEntry("hashes/files.sha256.json");
        var result = _svc.Verify(amp);

        Assert.False(result.HashFilePresent);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Hashes");
    }

    // ── 7. Missing curation/exclusions.json ──────────────────────────────────

    [Fact]
    public void Verify_MissingExclusions_ReturnsWarning()
    {
        var amp    = CreateAmpWithoutEntry("curation/exclusions.json");
        var result = _svc.Verify(amp);

        Assert.False(result.HasErrors);
        Assert.True(result.HasWarnings);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Warning &&
            i.Area     == "Exclusions");
    }

    // ── 8. Missing curation/notes.json ───────────────────────────────────────

    [Fact]
    public void Verify_MissingNotes_ReturnsWarning()
    {
        var amp    = CreateAmpWithoutEntry("curation/notes.json");
        var result = _svc.Verify(amp);

        Assert.False(result.HasErrors);
        Assert.True(result.HasWarnings);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Warning &&
            i.Area     == "Notes");
    }

    // ── 9. Backslash in archive path ──────────────────────────────────────────

    [Fact]
    public void Verify_BackslashInPath_ReturnsError()
    {
        var amp    = CreateAmpWithExtraEntry(@"media\bad\entry.png", [0x01, 0x02, 0x03]);
        var result = _svc.Verify(amp);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Paths" &&
            i.Message.Contains("backslash"));
    }

    // ── 10. Path traversal ────────────────────────────────────────────────────

    [Fact]
    public void Verify_PathTraversal_ReturnsError()
    {
        var amp    = CreateAmpWithExtraEntry("media/../evil.png", [0x01, 0x02, 0x03]);
        var result = _svc.Verify(amp);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Paths" &&
            i.Message.Contains("traversal"));
    }

    // ── 11. Corrupt manifest.json ─────────────────────────────────────────────

    [Fact]
    public void Verify_CorruptManifest_ReturnsError()
    {
        var amp    = CreateAmpWithCorruptEntry("manifest.json");
        var result = _svc.Verify(amp);

        Assert.True(result.ManifestPresent);
        Assert.False(result.ManifestValid);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Manifest" &&
            i.Message.Contains("not valid JSON"));
    }

    // ── 12. Corrupt releases.json ─────────────────────────────────────────────

    [Fact]
    public void Verify_CorruptReleases_ReturnsError()
    {
        var amp    = CreateAmpWithCorruptEntry("releases.json");
        var result = _svc.Verify(amp);

        Assert.True(result.ReleasesPresent);
        Assert.False(result.ReleasesValid);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Releases" &&
            i.Message.Contains("not valid JSON"));
    }

    // ── 13. Corrupt hashes/files.sha256.json ─────────────────────────────────

    [Fact]
    public void Verify_CorruptHashFile_ReturnsError()
    {
        var amp    = CreateAmpWithCorruptEntry("hashes/files.sha256.json");
        var result = _svc.Verify(amp);

        Assert.True(result.HashFilePresent);
        Assert.False(result.HashFileValid);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Hashes" &&
            i.Message.Contains("not valid JSON"));
    }

    // ── 14. Wrong FormatName ──────────────────────────────────────────────────

    [Fact]
    public void Verify_WrongFormatName_ReturnsError()
    {
        var badManifest = JsonSerializer.Serialize(new
        {
            FormatName       = "wrong-name",
            FormatVersion    = "1",
            CreatedAtUtc     = DateTime.UtcNow.ToString("O"),
            HardwareFamilyId = "snes",
            DatLineId        = "snes-nointro",
            SystemName       = "Super Nintendo",
            ReleaseCount     = 1,
            MediaFileCount   = 1,
            TotalMediaBytes  = 8L,
            ExclusionCount   = 0,
            ExtraNotesCount  = 0,
        }, new JsonSerializerOptions { WriteIndented = true });

        var amp    = CreateAmpWithReplacedJson("manifest.json", badManifest);
        var result = _svc.Verify(amp);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Manifest" &&
            i.Message.Contains("FormatName"));
    }

    // ── 15. Wrong FormatVersion ───────────────────────────────────────────────

    [Fact]
    public void Verify_WrongFormatVersion_ReturnsWarning()
    {
        var badManifest = JsonSerializer.Serialize(new
        {
            FormatName       = "Arkadia Media Pack",
            FormatVersion    = "99",
            CreatedAtUtc     = DateTime.UtcNow.ToString("O"),
            HardwareFamilyId = "snes",
            DatLineId        = "snes-nointro",
            SystemName       = "Super Nintendo",
            ReleaseCount     = 1,
            MediaFileCount   = 1,
            TotalMediaBytes  = 8L,
            ExclusionCount   = 0,
            ExtraNotesCount  = 0,
        }, new JsonSerializerOptions { WriteIndented = true });

        var amp    = CreateAmpWithReplacedJson("manifest.json", badManifest);
        var result = _svc.Verify(amp);

        Assert.True(result.HasWarnings);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Warning &&
            i.Area     == "Manifest" &&
            i.Message.Contains("FormatVersion"));
    }

    // ── 16. Duplicate ReleaseId ───────────────────────────────────────────────

    [Fact]
    public void Verify_DuplicateReleaseId_ReturnsError()
    {
        var badReleases = JsonSerializer.Serialize(new[]
        {
            new { ReleaseId = "rel-001", DatName = "Game A", Media = Array.Empty<object>() },
            new { ReleaseId = "rel-001", DatName = "Game B", Media = Array.Empty<object>() },
        }, new JsonSerializerOptions { WriteIndented = true });

        var amp    = CreateAmpWithReplacedJson("releases.json", badReleases);
        var result = _svc.Verify(amp);

        Assert.True(result.HasErrors);
        Assert.Equal(1, result.DuplicateReleaseKeys);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Releases" &&
            i.Message.Contains("Duplicate ReleaseId"));
    }

    // ── 17. Duplicate ArchivePath ─────────────────────────────────────────────

    [Fact]
    public void Verify_DuplicateArchivePath_ReturnsError()
    {
        var badReleases = JsonSerializer.Serialize(new[]
        {
            new
            {
                ReleaseId = "rel-001", DatName = "Game A",
                Media = new[]
                {
                    new { MediaType = "cover-front", ArchivePath = "media/cover-front/rel-001/cover.png",
                          FileName = "cover.png", Sha256 = new string('a', 64), SizeBytes = 8L },
                    new { MediaType = "cover-front", ArchivePath = "media/cover-front/rel-001/cover.png",
                          FileName = "cover.png", Sha256 = new string('a', 64), SizeBytes = 8L },
                }
            }
        }, new JsonSerializerOptions { WriteIndented = true });

        var amp    = CreateAmpWithReplacedJson("releases.json", badReleases);
        var result = _svc.Verify(amp);

        Assert.True(result.HasErrors);
        Assert.Equal(1, result.DuplicateArchivePaths);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Releases" &&
            i.Message.Contains("Duplicate ArchivePath"));
    }

    // ── 18. Count mismatch (manifest vs releases) ─────────────────────────────

    [Fact]
    public void Verify_ReleaseCountMismatch_ReturnsWarning()
    {
        var badManifest = JsonSerializer.Serialize(new
        {
            FormatName       = "Arkadia Media Pack",
            FormatVersion    = "1",
            CreatedAtUtc     = DateTime.UtcNow.ToString("O"),
            HardwareFamilyId = "snes",
            DatLineId        = "snes-nointro",
            SystemName       = "Super Nintendo",
            ReleaseCount     = 99,   // wrong
            MediaFileCount   = 1,
            TotalMediaBytes  = 8L,
            ExclusionCount   = 0,
            ExtraNotesCount  = 0,
        }, new JsonSerializerOptions { WriteIndented = true });

        var amp    = CreateAmpWithReplacedJson("manifest.json", badManifest);
        var result = _svc.Verify(amp);

        Assert.True(result.HasWarnings);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Warning &&
            i.Area     == "Consistency" &&
            i.Message.Contains("ReleaseCount"));
    }

    // ── 19. Media missing from ZIP ────────────────────────────────────────────

    [Fact]
    public void Verify_MediaMissingFromZip_ReturnsWarning()
    {
        var badReleases = JsonSerializer.Serialize(new[]
        {
            new
            {
                ReleaseId = "rel-001", DatName = "Game A",
                Media = new[]
                {
                    new { MediaType = "cover-front",
                          ArchivePath = "media/cover-front/rel-001/phantom.png",
                          FileName    = "phantom.png",
                          Sha256      = new string('b', 64),
                          SizeBytes   = 8L }
                }
            }
        }, new JsonSerializerOptions { WriteIndented = true });

        var amp    = CreateAmpWithReplacedJson("releases.json", badReleases);
        var result = _svc.Verify(amp);

        Assert.True(result.HasWarnings);
        Assert.Equal(1, result.MediaFilesMissing);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Warning &&
            i.Area     == "Media" &&
            i.Message.Contains("phantom.png"));
    }

    // ── 20. Zero-byte media file ──────────────────────────────────────────────

    [Fact]
    public void Verify_ZeroByteMediaEntry_ReturnsError()
    {
        const string zeroEntryPath = "media/cover-front/rel-001/empty.png";

        // Build a minimal, self-contained ZIP from scratch so we have full control.
        var amp = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".amp");

        var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            FormatName       = "Arkadia Media Pack",
            FormatVersion    = "1",
            CreatedAtUtc     = DateTime.UtcNow.ToString("O"),
            HardwareFamilyId = "snes",
            DatLineId        = "snes-nointro",
            SystemName       = "Super Nintendo",
            ReleaseCount     = 1,
            MediaFileCount   = 1,
            TotalMediaBytes  = 0L,
            ExclusionCount   = 0,
            ExtraNotesCount  = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));

        var releases = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
        {
            new
            {
                ReleaseId = "rel-001", DatName = "Game A",
                Media = new[]
                {
                    new { MediaType = "cover-front",
                          ArchivePath = zeroEntryPath,
                          FileName    = "empty.png",
                          Sha256      = new string('0', 64),
                          SizeBytes   = 0L }
                }
            }
        }, new JsonSerializerOptions { WriteIndented = true }));

        var exclusions = Encoding.UTF8.GetBytes("[]");
        var notes      = Encoding.UTF8.GetBytes("[]");
        var hashes     = Encoding.UTF8.GetBytes("[]");

        using (var fs  = new FileStream(amp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void WriteBytes(string name, byte[] data)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(data, 0, data.Length);
            }

            WriteBytes("manifest.json",            manifest);
            WriteBytes("releases.json",             releases);
            WriteBytes("curation/exclusions.json",  exclusions);
            WriteBytes("curation/notes.json",        notes);
            WriteBytes("hashes/files.sha256.json",  hashes);

            // Write zero-byte media entry by opening and immediately closing
            var mediaEntry = zip.CreateEntry(zeroEntryPath, CompressionLevel.Optimal);
            mediaEntry.Open().Dispose();
        }

        var result = _svc.Verify(amp);

        Assert.True(result.HasErrors);
        Assert.Equal(1, result.ZeroByteMediaFiles);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Media" &&
            i.Message.Contains("zero bytes"));
    }

    // ── 21. SHA-256 mismatch in hash file ─────────────────────────────────────

    [Fact]
    public void Verify_Sha256Mismatch_ReturnsError()
    {
        // Replace hashes/files.sha256.json with a version having a wrong hash for releases.json
        var src = CreateValidAmpFile();

        // Read the original hash file to get entry structure, then corrupt one hash
        string originalHashJson;
        using (var srcZip = ZipFile.OpenRead(src))
        {
            var e = srcZip.GetEntry("hashes/files.sha256.json")!;
            using var s = e.Open();
            using var r = new System.IO.StreamReader(s);
            originalHashJson = r.ReadToEnd();
        }

        // Replace the sha256 value for "releases.json" entry
        var badHashJson = originalHashJson.Replace(
            "releases.json", "releases.json"); // keep path

        // Corrupt: inject a deliberately wrong hash array
        var wrongHashes = JsonSerializer.Serialize(new[]
        {
            new { Path = "manifest.json",            Sha256 = new string('0', 64), SizeBytes = 100L },
            new { Path = "releases.json",            Sha256 = new string('0', 64), SizeBytes = 100L },
            new { Path = "curation/exclusions.json", Sha256 = new string('0', 64), SizeBytes = 100L },
            new { Path = "curation/notes.json",      Sha256 = new string('0', 64), SizeBytes = 100L },
        }, new JsonSerializerOptions { WriteIndented = true });

        var amp    = CreateAmpWithReplacedJson("hashes/files.sha256.json", wrongHashes);
        var result = _svc.Verify(amp);

        Assert.True(result.HasErrors);
        Assert.True(result.Sha256Mismatches > 0);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "Hashes" &&
            i.Message.Contains("SHA-256 mismatch"));
    }

    // ── 22. Forbidden content — ssuser (Error) ────────────────────────────────

    [Fact]
    public void Verify_ForbiddenContent_SsUser_ReturnsError()
    {
        var badReleases = """
            [{"ReleaseId":"rel-001","DatName":"Game","Media":[],"ssuser":"leaky"}]
            """;

        var amp    = CreateAmpWithReplacedJson("releases.json", badReleases);
        var result = _svc.Verify(amp);

        Assert.True(result.HasErrors);
        Assert.True(result.ForbiddenContentViolations > 0);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Area     == "ForbiddenContent");
    }

    // ── 23. Forbidden content — screenscraper (Warning) ───────────────────────

    [Fact]
    public void Verify_ForbiddenContent_Screenscraper_ReturnsWarning()
    {
        var badReleases = """
            [{"ReleaseId":"rel-001","DatName":"Game","Media":[],"note":"from screenscraper"}]
            """;

        var amp    = CreateAmpWithReplacedJson("releases.json", badReleases);
        var result = _svc.Verify(amp);

        Assert.True(result.HasWarnings);
        Assert.True(result.ForbiddenContentViolations > 0);
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Warning &&
            i.Area     == "ForbiddenContent");
    }

    // ── 24. Missing Attribution block — Warning severity, not Error ──────────

    [Fact]
    public void Verify_MissingAttribution_IsWarningNotError()
    {
        // Replace manifest.json with one that has no Attribution block.
        // The hash file will be stale (hash mismatch errors expected), but we
        // only care that the Attribution issue is flagged as Warning, not Error.
        var noAttrManifest = JsonSerializer.Serialize(new
        {
            FormatName       = "Arkadia Media Pack",
            FormatVersion    = "1",
            CreatedAtUtc     = DateTime.UtcNow.ToString("O"),
            HardwareFamilyId = "snes",
            DatLineId        = "snes-nointro",
            SystemName       = "Super Nintendo",
            ReleaseCount     = 1,
            MediaFileCount   = 1,
            TotalMediaBytes  = 8L,
            ExclusionCount   = 0,
            ExtraNotesCount  = 0,
            // No Attribution field
        }, new JsonSerializerOptions { WriteIndented = true });

        var amp    = CreateAmpWithReplacedJson("manifest.json", noAttrManifest);
        var result = _svc.Verify(amp);

        // Attribution missing → Warning, never Error
        Assert.Contains(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Warning &&
            i.Area     == "Manifest" &&
            i.Message.Contains("Attribution"));
        Assert.DoesNotContain(result.Issues, i =>
            i.Severity == AmpPackageVerificationSeverity.Error &&
            i.Message.Contains("Attribution"));
    }

    // ── 25. ScreenScraper in approved Attribution — no ForbiddenContent warning

    [Fact]
    public void Verify_ScreenScraperInApprovedAttribution_DoesNotTriggerForbiddenWarning()
    {
        var amp    = CreateValidAmpFile(); // writer seeds default Attribution with "ScreenScraper community"
        var result = _svc.Verify(amp);

        Assert.False(result.HasErrors);
        Assert.Equal("Valid", result.Status);
        Assert.DoesNotContain(result.Issues, i => i.Area == "ForbiddenContent");
    }
}
