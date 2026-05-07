using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Library;
using Arkadia.Providers;
using Xunit;

namespace Arkadia.Tests.Providers;

/// <summary>
/// Integration tests for ScreenScraperImportService.
/// Verifies proposal saving, payload storage, JSON file writing, field normalisation,
/// and the media download summary contract — all without real HTTP calls.
/// </summary>
public sealed class ScreenScraperImportServiceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _dbPath;

    public ScreenScraperImportServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "data");
        Directory.CreateDirectory(_dataDir);

        var dbDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbDir);
        _dbPath = Path.Combine(dbDir, "releases.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }

    private ScreenScraperImportService Svc() => new(_dataDir);

    private LibraryEntry MakeEntry(string name = "TestGame") => new()
    {
        Name             = name,
        Platform         = "SNES",
        HardwareFamilyId = "snes",
        DatLineId        = "testdat",
        ReleaseId        = "rel-001",
        DbPath           = _dbPath,
        Status           = "Present",
        Region           = "us",
        Languages        = "EN",
        Format           = "ZIP",
        Size             = "1 MB",
        Tier             = "A",
    };

    private static ScreenScraperResult EmptyResult(string rawJson = "{}") =>
        new() { RawJson = rawJson };

    // ── Payload storage ───────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_SavesProviderPayload_ToDatabase()
    {
        var entry  = MakeEntry();
        var result = EmptyResult("""{"title":"Test"}""");

        await Svc().ImportAsync(entry, result, []);

        var payload = new DatLineStore(_dbPath).LoadProviderPayload("rel-001", "screenscraper");
        Assert.Equal("""{"title":"Test"}""", payload);
    }

    [Fact]
    public async Task ImportAsync_EmptyRawJson_SavesEmptyObject()
    {
        var entry  = MakeEntry();
        var result = EmptyResult(rawJson: "");

        await Svc().ImportAsync(entry, result, []);

        var payload = new DatLineStore(_dbPath).LoadProviderPayload("rel-001", "screenscraper");
        Assert.Equal("{}", payload);
    }

    // ── JSON file writing ─────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_WritesMetadataJsonFile()
    {
        var entry  = MakeEntry();
        var result = EmptyResult("""{"data":true}""");

        await Svc().ImportAsync(entry, result, []);

        var expectedPath = Path.Combine(
            MediaStore.DatLinePath(_dataDir, entry.HardwareFamilyId, entry.DatLineId),
            "metadata",
            $"{MediaStore.ReleaseStem(entry.Name)}_screenscraper.json");

        Assert.True(File.Exists(expectedPath), $"Expected file at: {expectedPath}");
        Assert.Equal("""{"data":true}""", await File.ReadAllTextAsync(expectedPath));
    }

    [Fact]
    public async Task ImportAsync_MetadataJsonFile_MatchesPayload()
    {
        var entry  = MakeEntry("Zelda");
        var json   = """{"name":"Zelda","year":"1991"}""";
        var result = EmptyResult(json);

        await Svc().ImportAsync(entry, result, []);

        var filePath = Path.Combine(
            MediaStore.DatLinePath(_dataDir, entry.HardwareFamilyId, entry.DatLineId),
            "metadata",
            $"{MediaStore.ReleaseStem(entry.Name)}_screenscraper.json");

        var onDisk = await File.ReadAllTextAsync(filePath);
        Assert.Equal(json, onDisk);
    }

    // ── Proposals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_SavesProposals_WithAcceptedFalse()
    {
        var entry  = MakeEntry();
        var result = new ScreenScraperResult { Title = "My Game", Developer = "Dev Corp" };

        await Svc().ImportAsync(entry, result, []);

        var proposals = new DatLineStore(_dbPath).LoadMetadataProposals("rel-001", "screenscraper");
        Assert.NotEmpty(proposals);
        Assert.All(proposals, p => Assert.False(p.Accepted));
    }

    [Fact]
    public async Task ImportAsync_ProposalValues_MatchResult()
    {
        var entry  = MakeEntry();
        var result = new ScreenScraperResult { Title = "Super Mario", Year = "1990" };

        await Svc().ImportAsync(entry, result, []);

        var proposals = new DatLineStore(_dbPath)
            .LoadMetadataProposals("rel-001", "screenscraper");

        var title = proposals.Find(p => p.Field == "title");
        var year  = proposals.Find(p => p.Field == "year");
        Assert.NotNull(title); Assert.Equal("Super Mario", title!.Value);
        Assert.NotNull(year);  Assert.Equal("1990",        year!.Value);
    }

    [Fact]
    public async Task ImportAsync_EmptyResultFields_AreNotProposed()
    {
        var entry  = MakeEntry();
        var result = new ScreenScraperResult { Title = "Game", Developer = "" };

        await Svc().ImportAsync(entry, result, []);

        var proposals = new DatLineStore(_dbPath)
            .LoadMetadataProposals("rel-001", "screenscraper");
        Assert.DoesNotContain(proposals, p => p.Field == "developer");
    }

    // ── Field normalisation ───────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_NormalizesGenre_BeforeSaving()
    {
        var entry    = MakeEntry();
        var result   = new ScreenScraperResult { Genre = "rpg" };
        var mappings = new List<MetadataValueMappingRecord>
        {
            new("genre", "rpg", "RPG", Enabled: true),
        };

        await Svc().ImportAsync(entry, result, mappings);

        var proposals = new DatLineStore(_dbPath)
            .LoadMetadataProposals("rel-001", "screenscraper");
        var genre = proposals.Find(p => p.Field == "genre");
        Assert.NotNull(genre);
        Assert.Equal("RPG", genre!.Value);
    }

    [Fact]
    public async Task ImportAsync_NormalizesReleaseType_BeforeSaving()
    {
        var entry    = MakeEntry();
        var result   = new ScreenScraperResult { Rating = "pegi16" };
        var mappings = new List<MetadataValueMappingRecord>
        {
            new("rating", "pegi16", "PEGI 16", Enabled: true),
        };

        await Svc().ImportAsync(entry, result, mappings);

        var proposals = new DatLineStore(_dbPath)
            .LoadMetadataProposals("rel-001", "screenscraper");
        var rating = proposals.Find(p => p.Field == "rating");
        Assert.NotNull(rating);
        Assert.Equal("PEGI 16", rating!.Value);
    }

    [Fact]
    public async Task ImportAsync_DisabledMapping_IsNotApplied()
    {
        var entry    = MakeEntry();
        var result   = new ScreenScraperResult { Genre = "rpg" };
        var mappings = new List<MetadataValueMappingRecord>
        {
            new("genre", "rpg", "RPG", Enabled: false),
        };

        await Svc().ImportAsync(entry, result, mappings);

        var proposals = new DatLineStore(_dbPath)
            .LoadMetadataProposals("rel-001", "screenscraper");
        var genre = proposals.Find(p => p.Field == "genre");
        Assert.NotNull(genre);
        Assert.Equal("rpg", genre!.Value); // mapping disabled → raw value
    }

    // ── Media download summary ────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ReturnsZeroCounts_WhenNoMedia()
    {
        var entry  = MakeEntry();
        var result = EmptyResult(); // all media collections default to []

        var summary = await Svc().ImportAsync(entry, result, []);

        Assert.Equal(0,     summary.Covers);
        Assert.Equal(0,     summary.Screenshots);
        Assert.Equal(0,     summary.Fanart);
        Assert.False(summary.GotVideo);
        Assert.Equal(0,     summary.Logos);
        Assert.Equal(0,     summary.Marquees);
        Assert.Equal(0,     summary.Flyers);
        Assert.Equal(0,     summary.Manuals);
        Assert.Equal(0,     summary.PhysicalMedia);
        Assert.Equal(0,     summary.PhysicalTexture);
    }

    // ── Progress reporting ────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ReportsProgress_ForProposalAndCoverPhases()
    {
        var entry    = MakeEntry();
        var result   = EmptyResult();
        var reported = new List<string>();

        await Svc().ImportAsync(entry, result, [],
            progress: new Progress<string>(msg => reported.Add(msg)));

        // Give the Progress<T> callback a chance to fire (it posts to sync-context)
        await Task.Delay(50);

        Assert.Contains("Saving provider proposals…", reported);
        Assert.Contains("Downloading covers…",        reported);
    }
}
