using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Arkadia.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class ScreenScraperCachePackageImporterTests : IDisposable
{
    private readonly string _dir;
    private readonly CatalogService _catalog;
    private readonly ScreenScraperCachePackageImporter _importer;

    private const string ValidManifest = """
        {
            "version": 1,
            "provider": "screenscraper",
            "cacheProviderId": "screenscraper-cache",
            "systemId": "75",
            "systemName": "Capcom Classics",
            "builtAtUtc": "2026-05-01T00:00:00Z",
            "gameCount": 2,
            "mediaTypes": ["screenshot", "video"]
        }
        """;

    private const string ValidCsv = """
        "Game ID";"Game Name"
        "39874";"1942"
        "39875";"1943 - The Battle Of Midway"
        """;

    public ScreenScraperCachePackageImporterTests()
    {
        _dir      = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _catalog  = new CatalogService(_dir);
        _importer = new ScreenScraperCachePackageImporter(_catalog);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── Fixture helpers ───────────────────────────────────────────────────────

    private static byte[] BuildZip(
        string? manifestJson,
        string? gamesCsv,
        (string Path, byte[] Content)[]? extras = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (manifestJson is not null)
                WriteEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(manifestJson));
            if (gamesCsv is not null)
                WriteEntry(zip, "gameslist.csv", Encoding.UTF8.GetBytes(gamesCsv));
            if (extras is not null)
                foreach (var (path, content) in extras)
                    WriteEntry(zip, path, content);
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string entryPath, byte[] content)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.NoCompression);
        using var s = entry.Open();
        s.Write(content);
    }

    private string SaveZip(byte[] data, string name = "test.zip")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private SqliteConnection OpenCatalogDb()
    {
        var conn = new SqliteConnection($"Data Source={_catalog.DbPath}");
        conn.Open();
        return conn;
    }

    private long ScalarLong(string sql)
    {
        using var conn = OpenCatalogDb();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidPackage_InsertsCachePackagesRow()
    {
        var zip    = SaveZip(BuildZip(ValidManifest, ValidCsv));
        var result = _importer.IndexPackage(zip);

        Assert.False(result.WasAlreadyIndexed);
        Assert.True(result.PackageId > 0);
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM cache_packages"));
    }

    [Fact]
    public void ValidCsv_ImportsGames()
    {
        var zip    = SaveZip(BuildZip(ValidManifest, ValidCsv));
        var result = _importer.IndexPackage(zip);

        Assert.Equal(2, result.GameCount);
        Assert.Equal(2L, ScalarLong("SELECT COUNT(*) FROM cache_package_games"));
    }

    [Fact]
    public void DuplicatePackagePath_ReturnsWasAlreadyIndexed()
    {
        var zip = SaveZip(BuildZip(ValidManifest, ValidCsv));
        _importer.IndexPackage(zip);

        var result = _importer.IndexPackage(zip);

        Assert.True(result.WasAlreadyIndexed);
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM cache_packages"));
        Assert.Equal(2L, ScalarLong("SELECT COUNT(*) FROM cache_package_games"));
    }

    [Fact]
    public void MissingManifest_ThrowsInvalidDataException()
    {
        var zip = SaveZip(BuildZip(null, ValidCsv)); // no manifest.json
        Assert.Throws<InvalidDataException>(() => _importer.IndexPackage(zip));
    }

    [Fact]
    public void InvalidManifestVersion_ThrowsInvalidDataException()
    {
        var manifest = ValidManifest.Replace("\"version\": 1", "\"version\": 2");
        var zip      = SaveZip(BuildZip(manifest, ValidCsv));
        Assert.Throws<InvalidDataException>(() => _importer.IndexPackage(zip));
    }

    [Fact]
    public void WrongProvider_ThrowsInvalidDataException()
    {
        var manifest = ValidManifest.Replace("\"screenscraper\"", "\"launchbox\"");
        var zip      = SaveZip(BuildZip(manifest, ValidCsv));
        Assert.Throws<InvalidDataException>(() => _importer.IndexPackage(zip));
    }

    [Fact]
    public void CsvWithEmptyGameId_SkipsRow()
    {
        const string csv = """
            "Game ID";"Game Name"
            "39874";"1942"
            "";"Empty ID Game"
            "39875";"1943"
            """;
        var zip    = SaveZip(BuildZip(ValidManifest, csv));
        var result = _importer.IndexPackage(zip);

        Assert.Equal(2, result.GameCount);
    }

    [Fact]
    public void CsvWithEmptyGameName_SkipsRow()
    {
        const string csv = """
            "Game ID";"Game Name"
            "39874";"1942"
            "99999";""
            """;
        var zip    = SaveZip(BuildZip(ValidManifest, csv));
        var result = _importer.IndexPackage(zip);

        Assert.Equal(1, result.GameCount);
    }

    [Fact]
    public void PayloadEntry_SetsHasPayload()
    {
        var extras = new (string, byte[])[] { ("payloads/39874.json", "{}"u8.ToArray()) };
        var zip    = SaveZip(BuildZip(ValidManifest, ValidCsv, extras));
        _importer.IndexPackage(zip);

        Assert.Equal(1L, ScalarLong("SELECT has_payload FROM cache_package_games WHERE provider_game_id='39874'"));
        Assert.Equal(0L, ScalarLong("SELECT has_payload FROM cache_package_games WHERE provider_game_id='39875'"));
    }

    [Fact]
    public void MediaEntry_SetsHasMedia()
    {
        var extras = new (string, byte[])[] { ("media/screenshot/39874_0.png", []) };
        var zip    = SaveZip(BuildZip(ValidManifest, ValidCsv, extras));
        _importer.IndexPackage(zip);

        Assert.Equal(1L, ScalarLong("SELECT has_media FROM cache_package_games WHERE provider_game_id='39874'"));
        Assert.Equal(0L, ScalarLong("SELECT has_media FROM cache_package_games WHERE provider_game_id='39875'"));
    }

    [Fact]
    public void MediaEntries_IndexedWithCorrectMetadata()
    {
        var extras = new (string, byte[])[]
        {
            ("media/cover-front/39874_us_0.jpg", []),
            ("media/screenshot/39874_0.png",     []),
        };
        var zip = SaveZip(BuildZip(ValidManifest, ValidCsv, extras));
        _importer.IndexPackage(zip);

        using var conn = OpenCatalogDb();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT media_type, region, index_n, file_ext, zip_entry
            FROM cache_package_media
            WHERE provider_game_id = '39874'
            ORDER BY media_type
            """;
        using var r = cmd.ExecuteReader();

        Assert.True(r.Read());
        Assert.Equal("cover-front",                    r.GetString(0));
        Assert.Equal("us",                             r.GetString(1));
        Assert.Equal(0,                                (int)r.GetInt64(2));
        Assert.Equal("jpg",                            r.GetString(3));
        Assert.Equal("media/cover-front/39874_us_0.jpg", r.GetString(4));

        Assert.True(r.Read());
        Assert.Equal("screenshot",                r.GetString(0));
        Assert.Equal("",                          r.GetString(1));
        Assert.Equal(0,                           (int)r.GetInt64(2));
        Assert.Equal("png",                       r.GetString(3));
        Assert.Equal("media/screenshot/39874_0.png", r.GetString(4));

        Assert.False(r.Read());
    }

    [Fact]
    public void UnparseableMediaFilename_IsSkipped()
    {
        var extras = new (string, byte[])[]
        {
            ("media/screenshot/notamatch.png", []),  // no numeric prefix
            ("media/screenshot/39874_0.png",   []),  // valid
        };
        var zip    = SaveZip(BuildZip(ValidManifest, ValidCsv, extras));
        var result = _importer.IndexPackage(zip);

        Assert.Equal(1, result.MediaCount);
    }

    [Fact]
    public void Import_DoesNotExtractFiles()
    {
        var extras = new (string, byte[])[]
        {
            ("media/screenshot/39874_0.png",  [1, 2, 3]),
            ("payloads/39874.json",           """{"id":39874}"""u8.ToArray()),
        };
        var zip = SaveZip(BuildZip(ValidManifest, ValidCsv, extras));
        _importer.IndexPackage(zip);

        Assert.False(Directory.Exists(Path.Combine(_dir, "media")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "payloads")));
    }

    [Fact]
    public void InvalidManifest_LeavesNoPartialPackageRows()
    {
        var badManifest = """{ "version": 2 }""";
        var zip         = SaveZip(BuildZip(badManifest, ValidCsv));

        try { _importer.IndexPackage(zip); } catch (InvalidDataException) { }

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM cache_packages"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM cache_package_games"));
    }
}
