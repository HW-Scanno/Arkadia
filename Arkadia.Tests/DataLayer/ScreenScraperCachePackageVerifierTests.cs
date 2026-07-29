using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class ScreenScraperCachePackageVerifierTests : IDisposable
{
    private readonly string _dir;
    private readonly CatalogService _catalog;
    private readonly ScreenScraperCachePackageImporter _importer;
    private readonly ScreenScraperCachePackageVerifier _verifier;

    private const string Manifest = """
        {
            "version": 1,
            "provider": "screenscraper",
            "cacheProviderId": "screenscraper-cache",
            "systemId": "75",
            "systemName": "Capcom",
            "builtAtUtc": "2026-05-01T00:00:00Z",
            "gameCount": 2,
            "mediaTypes": ["screenshot"]
        }
        """;

    private const string Csv = """
        "Game ID";"Game Name"
        "101";"Game One"
        "102";"Game Two"
        """;

    // Sanitized payload — placeholders in URLs, no ssuser
    private const string ValidPayload101 = """
        {"response":{"jeu":{"id":"101","noms":[{"region":"wor","text":"Game One"}],"medias":{"jeu_ss":[{"url":"https://ss.fr/api?devid=<DEVID>&ssid=<SSID>&gameId=101"}]}}}}
        """;

    private const string ValidPayload102 = """
        {"response":{"jeu":{"id":"102","noms":[{"region":"wor","text":"Game Two"}]}}}
        """;

    public ScreenScraperCachePackageVerifierTests()
    {
        _dir      = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _catalog  = new CatalogService(_dir);
        _importer = new ScreenScraperCachePackageImporter(_catalog);
        _verifier = new ScreenScraperCachePackageVerifier(_catalog);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BuildZip(
        string? manifest,
        string? csv,
        (string Path, byte[] Content)[]? extras = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (manifest is not null) AddEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest));
            if (csv      is not null) AddEntry(zip, "gameslist.csv", Encoding.UTF8.GetBytes(csv));
            if (extras   is not null)
                foreach (var (p, c) in extras) AddEntry(zip, p, c);
        }
        return ms.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string entry, byte[] bytes)
    {
        var e = zip.CreateEntry(entry, CompressionLevel.NoCompression);
        using var s = e.Open();
        s.Write(bytes);
    }

    private string SaveZip(byte[] data, string name = "test.zip")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private int IndexZip(byte[] data, string name = "test.zip")
    {
        var path = SaveZip(data, name);
        return _importer.IndexPackage(path).PackageId;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    // 1. Valid package → Status = Valid
    [Fact]
    public void Verify_ValidPackage_ReturnsValid()
    {
        var id = IndexZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));

        var r = _verifier.Verify(id);

        Assert.Equal("Valid", r.Status);
        Assert.False(r.HasErrors);
        Assert.False(r.HasWarnings);
        Assert.Equal(2, r.PayloadJsonValid);
        Assert.Equal(2, r.PayloadsFound);
        Assert.Equal(0, r.PayloadsMissing);
    }

    // 2. Missing ZIP file → Error
    [Fact]
    public void Verify_MissingZipFile_ReturnsError()
    {
        var id = IndexZip(BuildZip(Manifest, Csv));
        File.Delete(Path.Combine(_dir, "test.zip"));

        var r = _verifier.Verify(id);

        Assert.Equal("Error", r.Status);
        Assert.False(r.FileExists);
        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Error && i.Area == "File");
    }

    // 3. Corrupt ZIP → Error
    [Fact]
    public void Verify_CorruptZip_ReturnsError()
    {
        var path = SaveZip(BuildZip(Manifest, Csv));
        var id   = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not a zip file"));

        var r = _verifier.Verify(id);

        Assert.Equal("Error", r.Status);
        Assert.True(r.FileExists);
        Assert.False(r.ZipReadable);
    }

    // 4. Missing manifest → Error
    [Fact]
    public void Verify_MissingManifest_ReturnsError()
    {
        var path = SaveZip(BuildZip(Manifest, Csv));
        var id   = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(null, Csv));

        var r = _verifier.Verify(id);

        Assert.Equal("Error", r.Status);
        Assert.False(r.ManifestPresent);
        Assert.Contains(r.Issues, i => i.Area == "Manifest");
    }

    // 5. Missing gameslist → Error
    [Fact]
    public void Verify_MissingGameslist_ReturnsError()
    {
        var path = SaveZip(BuildZip(Manifest, Csv));
        var id   = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, null));

        var r = _verifier.Verify(id);

        Assert.Equal("Error", r.Status);
        Assert.False(r.GamesListPresent);
        Assert.Contains(r.Issues, i => i.Area == "Gameslist");
    }

    // 6. has_payload game missing its payload entry → Warning
    [Fact]
    public void Verify_HasPayloadGameMissingEntry_ReturnsWarning()
    {
        var path = SaveZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));
        var id = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
        ]));

        var r = _verifier.Verify(id);

        Assert.Equal(1, r.PayloadsMissing);
        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Warning && i.Area == "Payload");
    }

    // 7. Zero-byte payload → Error
    [Fact]
    public void Verify_ZeroBytePayload_ReturnsError()
    {
        var path = SaveZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));
        var id = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Array.Empty<byte>()),
        ]));

        var r = _verifier.Verify(id);

        Assert.True(r.HasErrors);
        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Error &&
            i.Area     == "Payload" &&
            i.Message.Contains("zero bytes"));
    }

    // 8. Payload invalid JSON → Error
    [Fact]
    public void Verify_PayloadInvalidJson_ReturnsError()
    {
        var path = SaveZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));
        var id = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes("not json at all")),
        ]));

        var r = _verifier.Verify(id);

        Assert.True(r.HasErrors);
        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Error &&
            i.Area     == "Payload" &&
            i.Message.Contains("not valid JSON"));
    }

    // 9. Payload missing response.jeu → Error
    [Fact]
    public void Verify_PayloadMissingResponseJeu_ReturnsError()
    {
        var path = SaveZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));
        var id = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes("""{"response":{"serveurs":{}}}""")),
        ]));

        var r = _verifier.Verify(id);

        Assert.True(r.HasErrors);
        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Error &&
            i.Area     == "Payload" &&
            i.Message.Contains("response.jeu"));
    }

    // 10. Payload containing response.ssuser → Error (Sanitization)
    [Fact]
    public void Verify_PayloadContainsSsuser_ReturnsError()
    {
        const string withSsuser =
            """{"response":{"ssuser":{"id":"Scanno","numid":"99"},"jeu":{"id":"101","noms":[]}}}""";
        var path = SaveZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));
        var id = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(withSsuser)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));

        var r = _verifier.Verify(id);

        Assert.True(r.HasErrors);
        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Error &&
            i.Area     == "Sanitization" &&
            i.Message.Contains("ssuser"));
    }

    // 11. Payload URL with raw devpassword value → Error (Sanitization)
    [Fact]
    public void Verify_UnsanitizedCredential_ReturnsError()
    {
        const string unsanitized =
            """{"response":{"jeu":{"id":"101","medias":{"jeu_ss":[{"url":"?devpassword=MY_SECRET&gameId=101"}]}}}}""";
        var path = SaveZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));
        var id = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(unsanitized)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));

        var r = _verifier.Verify(id);

        Assert.True(r.SanitizationErrors > 0);
        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Error &&
            i.Area     == "Sanitization" &&
            i.Message.Contains("devpassword"));
    }

    // 12. Payload URL with placeholders → no sanitization error
    [Fact]
    public void Verify_PlaceholderCredentials_NoSanitizationError()
    {
        var id = IndexZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));

        var r = _verifier.Verify(id);

        Assert.Equal(0, r.SanitizationErrors);
        Assert.DoesNotContain(r.Issues, i => i.Area == "Sanitization");
    }

    // 13. Indexed media entry missing from ZIP → Warning
    [Fact]
    public void Verify_MediaMissingFromZip_ReturnsWarning()
    {
        var path = SaveZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json",          Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json",          Encoding.UTF8.GetBytes(ValidPayload102)),
            ("media/screenshot/101_0.jpg", new byte[512]),
        ]));
        var id = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));

        var r = _verifier.Verify(id);

        Assert.Equal(1, r.MediaFilesMissing);
        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Warning && i.Area == "Media");
    }

    // 14. Zero-byte media entry → Error
    [Fact]
    public void Verify_ZeroByteMedia_ReturnsError()
    {
        var path = SaveZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json",          Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json",          Encoding.UTF8.GetBytes(ValidPayload102)),
            ("media/screenshot/101_0.jpg", new byte[512]),
        ]));
        var id = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, Csv, [
            ("payloads/101.json",          Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json",          Encoding.UTF8.GetBytes(ValidPayload102)),
            ("media/screenshot/101_0.jpg", Array.Empty<byte>()),
        ]));

        var r = _verifier.Verify(id);

        Assert.Equal(1, r.ZeroByteMediaFiles);
        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Error && i.Area == "Media");
    }

    // NOTE: The verifier flags zero-byte media/payload files but does NOT warn on
    // suspiciously small (non-zero) files — that heuristic is intentionally not implemented.

    // 15. game_count field mismatch → Warning
    [Fact]
    public void Verify_GameCountMismatch_ReturnsWarning()
    {
        const string WrongCountManifest = """
            {
                "version": 1, "provider": "screenscraper", "cacheProviderId": "screenscraper-cache",
                "systemId": "75", "systemName": "Capcom",
                "builtAtUtc": "2026-05-01T00:00:00Z", "gameCount": 99, "mediaTypes": []
            }
            """;
        var id = IndexZip(BuildZip(WrongCountManifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));

        var r = _verifier.Verify(id);

        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Warning && i.Area == "Index");
    }

    // 16. gameslist row count mismatch → Warning
    [Fact]
    public void Verify_GameslistCountMismatch_ReturnsWarning()
    {
        const string ThreeGameCsv = """
            "Game ID";"Game Name"
            "101";"Game One"
            "102";"Game Two"
            "103";"Game Three"
            """;
        var path = SaveZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));
        var id = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, ThreeGameCsv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));

        var r = _verifier.Verify(id);

        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Warning && i.Area == "Gameslist");
    }

    // 17. response.jeu.id mismatch → Warning
    [Fact]
    public void Verify_JeuIdMismatch_ReturnsWarning()
    {
        const string wrongId =
            """{"response":{"jeu":{"id":"999","noms":[{"region":"wor","text":"Game One"}]}}}""";
        var path = SaveZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));
        var id = _importer.IndexPackage(path).PackageId;
        File.WriteAllBytes(path, BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(wrongId)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));

        var r = _verifier.Verify(id);

        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Warning &&
            i.Area     == "Payload" &&
            i.Message.Contains("mismatched"));
    }

    // 18. Valid media entries count correctly
    [Fact]
    public void Verify_ValidMediaCounted()
    {
        var id = IndexZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json",          Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json",          Encoding.UTF8.GetBytes(ValidPayload102)),
            ("media/screenshot/101_0.jpg", new byte[512]),
            ("media/screenshot/102_0.jpg", new byte[512]),
        ]));

        var r = _verifier.Verify(id);

        Assert.Equal(2, r.IndexedMediaCount);
        Assert.Equal(2, r.MediaFilesFound);
        Assert.Equal(0, r.MediaFilesMissing);
    }

    // 19. Extra files in ZIP do not fail verification
    [Fact]
    public void Verify_ExtraFilesInZip_DoNotFail()
    {
        var id = IndexZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
            ("extra/unknown.dat", new byte[8]),
            ("notes.txt",         Encoding.UTF8.GetBytes("build notes")),
        ]));

        var r = _verifier.Verify(id);

        Assert.Equal("Valid", r.Status);
    }

    // 20. Verify does not extract files (temp dir file count unchanged)
    [Fact]
    public void Verify_DoesNotExtractFiles()
    {
        var id = IndexZip(BuildZip(Manifest, Csv, [
            ("payloads/101.json", Encoding.UTF8.GetBytes(ValidPayload101)),
            ("payloads/102.json", Encoding.UTF8.GetBytes(ValidPayload102)),
        ]));

        var before = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Length;
        _verifier.Verify(id);
        var after = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Length;

        Assert.Equal(before, after);
    }

    // 21. Missing package row → Error result (no exception)
    [Fact]
    public void Verify_MissingPackageRow_ReturnsError()
    {
        var r = _verifier.Verify(999999);

        Assert.Equal("Error", r.Status);
        Assert.Contains(r.Issues, i =>
            i.Severity == CachePackageVerificationSeverity.Error && i.Area == "Index");
    }
}
