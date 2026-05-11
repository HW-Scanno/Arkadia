using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Ark;

public sealed class ArkPackageVerifierServiceTests : IDisposable
{
    private readonly string                      _baseDir;
    private readonly CatalogService              _catalog;
    private readonly ArkPackageVerifierService   _svc;

    public ArkPackageVerifierServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _catalog = new CatalogService(_baseDir);
        _svc     = new ArkPackageVerifierService();
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateValidArk()
    {
        var outPath = Path.Combine(_baseDir, "output", "test.ark");
        var result  = new ArkWriterService(_baseDir, _catalog).Write(
            new ArkExportOptions(IncludeAmpRegistry: false), outPath);
        return result.OutputPath;
    }

    private string CopyArkWithout(string src, string entryToRemove)
    {
        var dst = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".ark");
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

    private string CopyArkWithReplaced(string src, string entryName, byte[] newContent)
    {
        var dst = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".ark");
        using (var srcZip = ZipFile.OpenRead(src))
        using (var dstFs  = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var dstZip = new ZipArchive(dstFs, ZipArchiveMode.Create))
        {
            foreach (var entry in srcZip.Entries)
            {
                var newEntry = dstZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var dst2 = newEntry.Open();
                if (string.Equals(entry.FullName, entryName, StringComparison.Ordinal))
                    dst2.Write(newContent, 0, newContent.Length);
                else
                {
                    using var src2 = entry.Open();
                    src2.CopyTo(dst2);
                }
            }
        }
        return dst;
    }

    private string CopyArkWithExtra(string src, string extraEntryName, byte[] content)
    {
        var dst = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".ark");
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
        var result = _svc.Verify(Path.Combine(_baseDir, "nonexistent.ark"));

        Assert.False(result.FileExists);
        Assert.False(result.ZipReadable);
        Assert.True(result.HasErrors);
        Assert.Equal("Error", result.Status);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Error &&
            i.Area     == "File");
    }

    // ── 2. Not a ZIP ──────────────────────────────────────────────────────────

    [Fact]
    public void Verify_NotAZip_ReturnsError()
    {
        var path = Path.Combine(_baseDir, "bad.ark");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03]);

        var result = _svc.Verify(path);

        Assert.True(result.FileExists);
        Assert.False(result.ZipReadable);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Error &&
            i.Area     == "File");
    }

    // ── 3. Valid package ──────────────────────────────────────────────────────

    [Fact]
    public void Verify_ValidPackage_ReturnsValid()
    {
        var ark    = CreateValidArk();
        var result = _svc.Verify(ark);

        Assert.True(result.FileExists);
        Assert.True(result.ZipReadable);
        Assert.True(result.ManifestPresent);
        Assert.True(result.ManifestValid);
        Assert.True(result.HashFilePresent);
        Assert.True(result.HashFileValid);
        Assert.True(result.CatalogDbPresent);
        Assert.True(result.SidecarPresent);
        Assert.True(result.SidecarValid);
        Assert.False(result.HasErrors);
        Assert.False(result.HasWarnings);
        Assert.Equal("Valid", result.Status);
        Assert.Equal(0, result.Sha256Mismatches);
        Assert.Equal(0, result.UntrackedEntries);
    }

    // ── 4. Missing manifest.json ──────────────────────────────────────────────

    [Fact]
    public void Verify_MissingManifest_ReturnsError()
    {
        var src    = CreateValidArk();
        var ark    = CopyArkWithout(src, "manifest.json");
        var result = _svc.Verify(ark);

        Assert.False(result.ManifestPresent);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Error &&
            i.Area     == "Manifest");
    }

    // ── 5. Missing hashes/files.sha256.json ───────────────────────────────────

    [Fact]
    public void Verify_MissingHashFile_ReturnsError()
    {
        var src    = CreateValidArk();
        var ark    = CopyArkWithout(src, "hashes/files.sha256.json");
        var result = _svc.Verify(ark);

        Assert.False(result.HashFilePresent);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Error &&
            i.Area     == "Hashes");
    }

    // ── 6. Missing db/catalog.db ─────────────────────────────────────────────

    [Fact]
    public void Verify_MissingCatalogDb_ReturnsError()
    {
        var src    = CreateValidArk();
        var ark    = CopyArkWithout(src, "db/catalog.db");
        var result = _svc.Verify(ark);

        Assert.False(result.CatalogDbPresent);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Error &&
            i.Area     == "Catalog");
    }

    // ── 7. Wrong FormatName ───────────────────────────────────────────────────

    [Fact]
    public void Verify_ManifestWrongFormatName_ReturnsError()
    {
        var src = CreateValidArk();
        var badManifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            FormatName           = "wrong-name",
            FormatVersion        = "0.5",
            CreatedAtUtc         = DateTime.UtcNow.ToString("O"),
            ArkadiaAppVersion    = (string?)null,
            CredentialsExcluded  = true,
            CachePackagesExcluded = true,
            MediaIncluded        = false,
            AmpRegistryIncluded  = false,
            DatLineCount         = 0,
            StoreCount           = 1,
            HashAlgorithm        = "SHA-256",
        }, new JsonSerializerOptions { WriteIndented = true }));

        var ark    = CopyArkWithReplaced(src, "manifest.json", badManifest);
        var result = _svc.Verify(ark);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Error &&
            i.Area     == "Manifest" &&
            i.Message.Contains("FormatName"));
    }

    // ── 8. Wrong FormatVersion ────────────────────────────────────────────────

    [Fact]
    public void Verify_ManifestWrongFormatVersion_ReturnsWarning()
    {
        var src = CreateValidArk();
        var badManifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            FormatName           = "Arkadia Backup",
            FormatVersion        = "99",
            CreatedAtUtc         = DateTime.UtcNow.ToString("O"),
            ArkadiaAppVersion    = (string?)null,
            CredentialsExcluded  = true,
            CachePackagesExcluded = true,
            MediaIncluded        = false,
            AmpRegistryIncluded  = false,
            DatLineCount         = 0,
            StoreCount           = 1,
            HashAlgorithm        = "SHA-256",
        }, new JsonSerializerOptions { WriteIndented = true }));

        var ark    = CopyArkWithReplaced(src, "manifest.json", badManifest);
        var result = _svc.Verify(ark);

        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Warning &&
            i.Area     == "Manifest" &&
            i.Message.Contains("FormatVersion"));
    }

    // ── 9. Wrong HashAlgorithm ────────────────────────────────────────────────

    [Fact]
    public void Verify_ManifestWrongHashAlgorithm_ReturnsError()
    {
        var src = CreateValidArk();
        var badManifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            FormatName           = "Arkadia Backup",
            FormatVersion        = "0.5",
            CreatedAtUtc         = DateTime.UtcNow.ToString("O"),
            ArkadiaAppVersion    = (string?)null,
            CredentialsExcluded  = true,
            CachePackagesExcluded = true,
            MediaIncluded        = false,
            AmpRegistryIncluded  = false,
            DatLineCount         = 0,
            StoreCount           = 1,
            HashAlgorithm        = "MD5",
        }, new JsonSerializerOptions { WriteIndented = true }));

        var ark    = CopyArkWithReplaced(src, "manifest.json", badManifest);
        var result = _svc.Verify(ark);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Error &&
            i.Area     == "Manifest" &&
            i.Message.Contains("HashAlgorithm"));
    }

    // ── 10. SHA-256 mismatch in hash file ─────────────────────────────────────

    [Fact]
    public void Verify_Sha256Mismatch_ReturnsError()
    {
        var src = CreateValidArk();
        var wrongHashes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
        {
            new { Path = "manifest.json",  Sha256 = new string('0', 64), SizeBytes = 100L },
            new { Path = "db/catalog.db",  Sha256 = new string('0', 64), SizeBytes = 100L },
        }, new JsonSerializerOptions { WriteIndented = true }));

        var ark    = CopyArkWithReplaced(src, "hashes/files.sha256.json", wrongHashes);
        var result = _svc.Verify(ark);

        Assert.True(result.HasErrors);
        Assert.True(result.Sha256Mismatches > 0);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Error &&
            i.Area     == "Hashes" &&
            i.Message.Contains("SHA-256 mismatch"));
    }

    // ── 11. Untracked ZIP entry ───────────────────────────────────────────────

    [Fact]
    public void Verify_UntrackedZipEntry_ReturnsWarning()
    {
        var src    = CreateValidArk();
        var ark    = CopyArkWithExtra(src, "extra/unlisted.bin", [0x01, 0x02, 0x03]);
        var result = _svc.Verify(ark);

        Assert.True(result.UntrackedEntries > 0);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Warning &&
            i.Area     == "Hashes" &&
            i.Message.Contains("unlisted.bin"));
    }

    // ── 12. Backslash in archive path ─────────────────────────────────────────

    [Fact]
    public void Verify_BackslashInPath_ReturnsError()
    {
        var src    = CreateValidArk();
        var ark    = CopyArkWithExtra(src, @"db\bad\entry.db", [0x01, 0x02, 0x03]);
        var result = _svc.Verify(ark);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Error &&
            i.Area     == "Paths" &&
            i.Message.Contains("backslash"));
    }

    // ── 13. Path traversal ────────────────────────────────────────────────────

    [Fact]
    public void Verify_PathTraversal_ReturnsError()
    {
        var src    = CreateValidArk();
        var ark    = CopyArkWithExtra(src, "db/../evil.db", [0x01, 0x02, 0x03]);
        var result = _svc.Verify(ark);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Error &&
            i.Area     == "Paths" &&
            i.Message.Contains("traversal"));
    }

    // ── 14. Sidecar missing ───────────────────────────────────────────────────

    [Fact]
    public void Verify_SidecarMissing_ReturnsWarning()
    {
        var src = CreateValidArk();
        // Copy the .ark to a new path; the sidecar only exists next to the source.
        var ark = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".ark");
        File.Copy(src, ark, overwrite: false);
        Assert.False(File.Exists(ark + ".sha256"));

        var result = _svc.Verify(ark);

        Assert.False(result.SidecarPresent);
        Assert.Contains(result.Issues, i =>
            i.Severity == ArkPackageVerificationSeverity.Warning &&
            i.Area     == "Sidecar");
    }
}
