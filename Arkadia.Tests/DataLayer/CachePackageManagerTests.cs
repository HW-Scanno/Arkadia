using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Arkadia.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class CachePackageManagerTests : IDisposable
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
            "gameCount": 2
        }
        """;

    private const string ValidCsv = """
        "Game ID";"Game Name"
        "39874";"1942"
        "39875";"1943 - The Battle Of Midway"
        """;

    public CachePackageManagerTests()
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

    // ── ZIP helpers ───────────────────────────────────────────────────────────

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

    private string IndexValidPackage(
        string name = "test.zip",
        (string, byte[])[]? extras = null)
    {
        var zipPath = SaveZip(BuildZip(ValidManifest, ValidCsv, extras), name);
        _importer.IndexPackage(zipPath);
        return zipPath;
    }

    private long ScalarLong(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_catalog.DbPath}");
        conn.Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadCachePackages_ReturnsIndexedPackage()
    {
        var path = IndexValidPackage();
        var packages = _catalog.LoadCachePackages();

        Assert.Single(packages);
        Assert.Equal(path, packages[0].PackagePath);
        Assert.Equal("Capcom Classics", packages[0].SystemName);
        Assert.Equal("75", packages[0].SystemId);
    }

    [Fact]
    public void LoadCachePackages_ReportsGameCount()
    {
        IndexValidPackage();
        var packages = _catalog.LoadCachePackages();

        Assert.Equal(2, packages[0].GameCount);
    }

    [Fact]
    public void LoadCachePackages_ReportsMediaCount()
    {
        IndexValidPackage(extras:
        [
            ("payloads/39874.json", "{}"u8.ToArray()),
            ("media/screenshot/39874_0.png",     [1, 2, 3]),
            ("media/cover-front/39874_0.jpg",    [4, 5, 6]),
        ]);
        var packages = _catalog.LoadCachePackages();

        Assert.Equal(2, packages[0].MediaCount);
    }

    [Fact]
    public void LoadCachePackages_StatusAvailable_WhenFileExists()
    {
        IndexValidPackage();
        var packages = _catalog.LoadCachePackages();

        Assert.Equal("Available", packages[0].Status);
    }

    [Fact]
    public void LoadCachePackages_StatusMissing_WhenFileGone()
    {
        var path = IndexValidPackage();
        File.Delete(path);

        var packages = _catalog.LoadCachePackages();

        Assert.Equal("Missing", packages[0].Status);
    }

    [Fact]
    public void DetachCachePackage_RemovesPackageRow()
    {
        IndexValidPackage();
        var id = _catalog.LoadCachePackages()[0].Id;

        _catalog.DetachCachePackage(id);

        Assert.Empty(_catalog.LoadCachePackages());
    }

    [Fact]
    public void DetachCachePackage_CascadesGamesAndMedia()
    {
        IndexValidPackage(extras:
        [
            ("payloads/39874.json", "{}"u8.ToArray()),
            ("media/screenshot/39874_0.png", [1, 2, 3]),
        ]);
        var id = _catalog.LoadCachePackages()[0].Id;

        _catalog.DetachCachePackage(id);

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM cache_package_games"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM cache_package_media"));
    }

    [Fact]
    public void DetachMissingPackage_DoesNotRequireFileToExist()
    {
        var path = IndexValidPackage();
        var id   = _catalog.LoadCachePackages()[0].Id;
        File.Delete(path);

        _catalog.DetachCachePackage(id);

        Assert.Empty(_catalog.LoadCachePackages());
    }

    [Fact]
    public void RegisterExistingPackage_ReturnsWasAlreadyIndexed()
    {
        var path  = SaveZip(BuildZip(ValidManifest, ValidCsv));
        var first  = _importer.IndexPackage(path);
        var second = _importer.IndexPackage(path);

        Assert.False(first.WasAlreadyIndexed);
        Assert.True(second.WasAlreadyIndexed);
    }
}
