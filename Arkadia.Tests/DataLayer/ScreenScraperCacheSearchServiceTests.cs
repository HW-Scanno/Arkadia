using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class ScreenScraperCacheSearchServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly CatalogService _catalog;
    private readonly ScreenScraperCachePackageImporter _importer;
    private readonly ScreenScraperCacheSearchService _svc;

    private const string Manifest75 = """
        {
            "version": 1,
            "provider": "screenscraper",
            "cacheProviderId": "screenscraper-cache",
            "systemId": "75",
            "systemName": "Capcom Classics",
            "builtAtUtc": "2026-05-01T00:00:00Z",
            "gameCount": 3
        }
        """;

    private const string Manifest1 = """
        {
            "version": 1,
            "provider": "screenscraper",
            "cacheProviderId": "screenscraper-cache",
            "systemId": "1",
            "systemName": "Atari 2600",
            "builtAtUtc": "2026-05-01T00:00:00Z",
            "gameCount": 1
        }
        """;

    public ScreenScraperCacheSearchServiceTests()
    {
        _dir      = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _catalog  = new CatalogService(_dir);
        _importer = new ScreenScraperCachePackageImporter(_catalog);
        _svc      = new ScreenScraperCacheSearchService(_catalog);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── Fixture helpers ───────────────────────────────────────────────────────

    private string IndexPackage(
        string manifest,
        string csv,
        (string Path, byte[] Content)[]? extras = null,
        string zipName = "pkg.zip")
    {
        var zipPath = Path.Combine(_dir, zipName);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest));
            WriteEntry(zip, "gameslist.csv",  Encoding.UTF8.GetBytes(csv));
            if (extras is not null)
                foreach (var (p, c) in extras)
                    WriteEntry(zip, p, c);
        }
        File.WriteAllBytes(zipPath, ms.ToArray());
        _importer.IndexPackage(zipPath);
        return zipPath;
    }

    private static void WriteEntry(ZipArchive zip, string path, byte[] content)
    {
        var e = zip.CreateEntry(path, System.IO.Compression.CompressionLevel.NoCompression);
        using var s = e.Open();
        s.Write(content);
    }

    private const string Csv3 = """
        "Game ID";"Game Name"
        "1001";"1942"
        "1002";"1943 - The Battle of Midway"
        "1003";"Ghosts'n Goblins"
        """;

    private (string Path, byte[] Content)[] PayloadExtras(params string[] gameIds)
    {
        var result = new (string, byte[])[gameIds.Length];
        for (int i = 0; i < gameIds.Length; i++)
            result[i] = ($"payloads/{gameIds[i]}.json", Encoding.UTF8.GetBytes("{}"));
        return result;
    }

    private static byte[] RomPayload(params string[] romFilenames)
    {
        var romsJson = string.Join(",", System.Array.ConvertAll(romFilenames,
            f => $"{{\"romfilename\":\"{f}\"}}"));
        return Encoding.UTF8.GetBytes(
            $"{{\"response\":{{\"jeu\":{{\"noms\":[],\"roms\":[{romsJson}]}}}}}}");
    }

    private static byte[] NomPayload(params string[] names)
    {
        var nomsJson = string.Join(",", System.Array.ConvertAll(names,
            n => $"{{\"region\":\"wor\",\"text\":\"{n}\"}}"));
        return Encoding.UTF8.GetBytes(
            $"{{\"response\":{{\"jeu\":{{\"noms\":[{nomsJson}],\"roms\":[]}}}}}}");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Search_ExactTitleMatch_ReturnsCandidate()
    {
        IndexPackage(Manifest75, Csv3, PayloadExtras("1001", "1002", "1003"));

        var results = _svc.Search("1942");

        Assert.Contains(results, r => r.Title == "1942");
    }

    [Fact]
    public void Search_ExactMatch_AppearsBeforeContainsMatch()
    {
        IndexPackage(Manifest75, Csv3, PayloadExtras("1001", "1002", "1003"));

        var results = _svc.Search("1942");

        // "1942" exact match should come before "1943 - The Battle of Midway" (contains "194")
        Assert.True(results.Count >= 1);
        Assert.Equal("1942", results[0].Title);
    }

    [Fact]
    public void Search_ContainsMatch_ReturnsCandidates()
    {
        IndexPackage(Manifest75, Csv3, PayloadExtras("1001", "1002", "1003"));

        var results = _svc.Search("19");

        Assert.True(results.Count >= 2);
        Assert.Contains(results, r => r.Title == "1942");
        Assert.Contains(results, r => r.Title.StartsWith("1943"));
    }

    [Fact]
    public void Search_SameSystemId_AppearsFirst()
    {
        var csvAtari = """
            "Game ID";"Game Name"
            "2001";"Ghost'n Goblins Atari"
            """;
        IndexPackage(Manifest75,  Csv3,    PayloadExtras("1001", "1002", "1003"), "pkg75.zip");
        IndexPackage(Manifest1,   csvAtari, PayloadExtras("2001"), "pkg1.zip");

        // Searching "Goblins" with system 75 — Capcom package should prefer
        var results = _svc.Search("Goblins", systemId: "75");

        Assert.True(results.Count >= 2);
        Assert.Equal("75", results[0].SystemId);
    }

    [Fact]
    public void Search_MissingZipFile_IgnoresPackage()
    {
        var zipPath = IndexPackage(Manifest75, Csv3, PayloadExtras("1001", "1002", "1003"));
        File.Delete(zipPath);

        var results = _svc.Search("1942");

        Assert.Empty(results);
    }

    [Fact]
    public void Search_NoPayload_ExcludesCandidate()
    {
        // Index with no payload extras — all games have has_payload=0
        IndexPackage(Manifest75, Csv3);

        var results = _svc.Search("1942");

        Assert.Empty(results);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        IndexPackage(Manifest75, Csv3, PayloadExtras("1001", "1002", "1003"));

        var results = _svc.Search("");

        Assert.Empty(results);
    }

    [Fact]
    public void Search_CaseInsensitive()
    {
        IndexPackage(Manifest75, Csv3, PayloadExtras("1001", "1002", "1003"));

        var results = _svc.Search("GHOSTS");

        Assert.Contains(results, r => r.Title == "Ghosts'n Goblins");
    }

    [Fact]
    public void Search_NoPackages_ReturnsEmpty()
    {
        var results = _svc.Search("1942");

        Assert.Empty(results);
    }

    // ── Provider availability ─────────────────────────────────────────────────

    [Fact]
    public void HasUsableCachePackages_NoPackages_ReturnsFalse()
        => Assert.False(_catalog.HasUsableCachePackages());

    [Fact]
    public void HasUsableCachePackages_PackageWithPayload_ReturnsTrue()
    {
        IndexPackage(Manifest75, Csv3, PayloadExtras("1001"));

        Assert.True(_catalog.HasUsableCachePackages());
    }

    [Fact]
    public void HasUsableCachePackages_PackageNoPayload_ReturnsFalse()
    {
        IndexPackage(Manifest75, Csv3);

        Assert.False(_catalog.HasUsableCachePackages());
    }

    [Fact]
    public void HasUsableCachePackages_ZipDeleted_ReturnsFalse()
    {
        var zipPath = IndexPackage(Manifest75, Csv3, PayloadExtras("1001"));
        File.Delete(zipPath);

        Assert.False(_catalog.HasUsableCachePackages());
    }

    // ── Arcade / MAME shortname search ───────────────────────────────────────

    [Fact]
    public void Search_RomFilename_ExactStem_ReturnsCandidate()
    {
        var csv = """
            "Game ID";"Game Name"
            "5001";"The King of Fighters '98"
            """;
        IndexPackage(Manifest75, csv, [("payloads/5001.json", RomPayload("kofnw.zip"))]);

        var results = _svc.Search("kofnw");

        Assert.Contains(results, r => r.ProviderGameId == "5001");
    }

    [Fact]
    public void Search_RomFilename_WithExtension_ReturnsCandidate()
    {
        var csv = """
            "Game ID";"Game Name"
            "5001";"The King of Fighters '98"
            """;
        IndexPackage(Manifest75, csv, [("payloads/5001.json", RomPayload("kofnw.zip"))]);

        var results = _svc.Search("kofnw.zip");

        Assert.Contains(results, r => r.ProviderGameId == "5001");
    }

    [Fact]
    public void Search_RomFilename_ExactStem_AppearsBeforeTitleContainsMatch()
    {
        // Game A: title "kofnw Cup" (title-contains match, rank 2)
        // Game B: romfilename "kofnw.zip" (exact romfilename match, rank 0)
        var csv = """
            "Game ID";"Game Name"
            "5001";"kofnw Cup"
            "5002";"The King of Fighters '98"
            """;
        IndexPackage(Manifest75, csv, [
            ("payloads/5001.json", Encoding.UTF8.GetBytes("{}")),
            ("payloads/5002.json", RomPayload("kofnw.zip")),
        ]);

        var results = _svc.Search("kofnw");

        Assert.True(results.Count >= 2);
        Assert.Equal("5002", results[0].ProviderGameId);
    }

    [Fact]
    public void Search_AltName_ReturnsCandidate()
    {
        var csv = """
            "Game ID";"Game Name"
            "5001";"Street Fighter II"
            """;
        IndexPackage(Manifest75, csv, [("payloads/5001.json", NomPayload("Street Fighter 2"))]);

        var results = _svc.Search("Street Fighter 2");

        Assert.Contains(results, r => r.ProviderGameId == "5001");
    }

    [Fact]
    public void Search_AltName_CaseInsensitive()
    {
        var csv = """
            "Game ID";"Game Name"
            "5001";"Street Fighter II"
            """;
        IndexPackage(Manifest75, csv, [("payloads/5001.json", NomPayload("Street Fighter 2"))]);

        var results = _svc.Search("STREET FIGHTER 2");

        Assert.Contains(results, r => r.ProviderGameId == "5001");
    }

    [Fact]
    public void Search_NoSearchTerms_FallsBackToTitle()
    {
        // Package with no payload JSON content (empty `{}`) — no search terms extracted from payload
        // but title-based search should still work as backward-compat fallback
        var csv = """
            "Game ID";"Game Name"
            "5001";"Pac-Man"
            """;
        IndexPackage(Manifest75, csv, [("payloads/5001.json", Encoding.UTF8.GetBytes("{}"))]);

        var results = _svc.Search("Pac-Man");

        Assert.Contains(results, r => r.Title == "Pac-Man");
    }

    [Fact]
    public void Search_PartialRomName_ReturnsCandidate()
    {
        var csv = """
            "Game ID";"Game Name"
            "5001";"The King of Fighters '98"
            """;
        IndexPackage(Manifest75, csv, [("payloads/5001.json", RomPayload("kofnw.zip"))]);

        var results = _svc.Search("kof");

        Assert.Contains(results, r => r.ProviderGameId == "5001");
    }

    [Fact]
    public void Search_MultipleRoms_MatchesAny()
    {
        var csv = """
            "Game ID";"Game Name"
            "5001";"Some Arcade Game"
            """;
        IndexPackage(Manifest75, csv, [("payloads/5001.json", RomPayload("game1.zip", "game1r1.zip", "game1r2.zip"))]);

        var r1 = _svc.Search("game1r2");
        var r2 = _svc.Search("game1r1");

        Assert.Contains(r1, r => r.ProviderGameId == "5001");
        Assert.Contains(r2, r => r.ProviderGameId == "5001");
    }

    [Fact]
    public void Search_TitleAndRomTerm_Deduplicated()
    {
        // The game's title contains "kofnw" AND has romfilename "kofnw.zip"
        // It should appear exactly once in results
        var csv = """
            "Game ID";"Game Name"
            "5001";"kofnw"
            """;
        IndexPackage(Manifest75, csv, [("payloads/5001.json", RomPayload("kofnw.zip"))]);

        var results = _svc.Search("kofnw");

        Assert.Equal(1, results.Count(r => r.ProviderGameId == "5001"));
    }

    [Fact]
    public void Search_RomFilenameExact_RanksAboveExactTitleOtherGame()
    {
        // Game A: title exactly "mslug" (rank 1 = exact title)
        // Game B: romfilename "mslug.zip" (rank 0 = exact romfilename term)
        var csv = """
            "Game ID";"Game Name"
            "5001";"mslug"
            "5002";"Metal Slug"
            """;
        IndexPackage(Manifest75, csv, [
            ("payloads/5001.json", Encoding.UTF8.GetBytes("{}")),
            ("payloads/5002.json", RomPayload("mslug.zip")),
        ]);

        var results = _svc.Search("mslug");

        Assert.True(results.Count >= 2);
        Assert.Equal("5002", results[0].ProviderGameId);
    }
}
