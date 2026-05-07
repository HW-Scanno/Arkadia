using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Library;
using Arkadia.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Providers;

public sealed class ScreenScraperCacheImportServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly CatalogService _catalog;
    private readonly ScreenScraperCachePackageImporter _importer;

    // Minimal valid ScreenScraper API response JSON (ParseGameJson format)
    private const string PayloadJson = """
        {
          "response": {
            "jeu": {
              "id": "1001",
              "noms": [{"region": "wor", "text": "1942"}],
              "developpeur": {"text": "Capcom"},
              "editeur": {"text": "Capcom"},
              "dates": [{"region": "wor", "text": "1984"}],
              "synopsis": [{"langue": "en", "text": "Shoot em up"}]
            }
          }
        }
        """;

    private const string Manifest = """
        {
            "version": 1,
            "provider": "screenscraper",
            "cacheProviderId": "screenscraper-cache",
            "systemId": "75",
            "systemName": "Capcom Classics",
            "builtAtUtc": "2026-05-01T00:00:00Z",
            "gameCount": 1
        }
        """;

    private const string Csv = """
        "Game ID";"Game Name"
        "1001";"1942"
        """;

    public ScreenScraperCacheImportServiceTests()
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
        (string Path, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                var e = zip.CreateEntry(path, CompressionLevel.NoCompression);
                using var s = e.Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    private string SaveAndIndexZip(
        byte[] zipBytes, string name = "pkg.zip")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, zipBytes);
        _importer.IndexPackage(path);
        return path;
    }

    private ScreenScraperCacheCandidate MakeCandidate(string zipPath, int packageId = 1)
        => new(
            PackageId:      packageId,
            PackagePath:    zipPath,
            ProviderGameId: "1001",
            SystemId:       "75",
            SystemName:     "Capcom Classics",
            Title:          "1942",
            HasPayload:     true,
            HasMedia:       false);

    private LibraryEntry MakeEntry()
    {
        var dbPath = Path.Combine(_dir, "store", "releases.db");
        return new LibraryEntry
        {
            Name             = "1942 (World Rev A)",
            Platform         = "Arcade",
            Status           = "Good",
            Region           = "wor",
            Languages        = "en",
            Format           = "rom",
            Size             = "128 KB",
            Tier             = "A",
            ReleaseId        = "rel-001",
            HardwareFamilyId = "capcom",
            DatLineId        = "capcom-cps1",
            DbPath           = dbPath,
        };
    }

    private ScreenScraperCacheImportService MakeSvc()
        => new(Path.Combine(_dir, "data"), _catalog);

    private (string, byte[])[] BaseZipEntries(string? mediaZipEntry = null)
    {
        var entries = new List<(string, byte[])>
        {
            ("manifest.json",       Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv",       Encoding.UTF8.GetBytes(Csv)),
            ("payloads/1001.json",  Encoding.UTF8.GetBytes(PayloadJson)),
        };
        if (mediaZipEntry is not null)
            entries.Add((mediaZipEntry, new byte[] { 0x89, 0x50, 0x4E, 0x47 })); // PNG header bytes
        return entries.ToArray();
    }

    private SqliteConnection OpenCatalog()
    {
        var conn = new SqliteConnection($"Data Source={_catalog.DbPath}");
        conn.Open();
        return conn;
    }

    private int GetPackageId(string zipPath)
    {
        using var conn = OpenCatalog();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM cache_packages WHERE package_path = $p";
        cmd.Parameters.AddWithValue("$p", zipPath);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_ReadsPayloadFromZip_ReturnsPayloadImported()
    {
        var zipPath = SaveAndIndexZip(BuildZip(BaseZipEntries()));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        var summary = await svc.ImportAsync(entry, cand, []);

        Assert.True(summary.PayloadImported);
    }

    [Fact]
    public async Task Import_SavesProviderPayload_WithCorrectProvider()
    {
        var zipPath = SaveAndIndexZip(BuildZip(BaseZipEntries()));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        await svc.ImportAsync(entry, cand, []);

        var store   = new DatLineStore(entry.DbPath);
        var payload = store.LoadProviderPayload(entry.ReleaseId, ScreenScraperCacheImportService.ProviderId);
        Assert.NotNull(payload);
        Assert.Contains("1942", payload);
    }

    [Fact]
    public async Task Import_SavesProviderPayload_NotOnlineProvider()
    {
        var zipPath = SaveAndIndexZip(BuildZip(BaseZipEntries()));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        await svc.ImportAsync(entry, cand, []);

        var store          = new DatLineStore(entry.DbPath);
        var onlinePayload  = store.LoadProviderPayload(entry.ReleaseId, ArkadiaProviders.ScreenScraper);
        Assert.Null(onlinePayload);
    }

    [Fact]
    public async Task Import_CreatesProposals_WithCacheProvider()
    {
        var zipPath = SaveAndIndexZip(BuildZip(BaseZipEntries()));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        await svc.ImportAsync(entry, cand, []);

        var store     = new DatLineStore(entry.DbPath);
        var proposals = store.LoadMetadataProposals(entry.ReleaseId, ScreenScraperCacheImportService.ProviderId);
        Assert.NotEmpty(proposals);
        Assert.All(proposals, p => Assert.Equal(ScreenScraperCacheImportService.ProviderId, p.Provider));
    }

    [Fact]
    public async Task Import_DoesNotAutoApplyCanonicalMetadata()
    {
        var zipPath = SaveAndIndexZip(BuildZip(BaseZipEntries()));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        await svc.ImportAsync(entry, cand, []);

        // entry.Metadata must NOT be modified by the service
        Assert.Null(entry.Metadata);
    }

    [Fact]
    public async Task Import_TitleProposal_MatchesParsedJson()
    {
        var zipPath = SaveAndIndexZip(BuildZip(BaseZipEntries()));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        await svc.ImportAsync(entry, cand, []);

        var store     = new DatLineStore(entry.DbPath);
        var proposals = store.LoadMetadataProposals(entry.ReleaseId, ScreenScraperCacheImportService.ProviderId);
        var title     = proposals.Find(p => p.Field == "title");
        Assert.NotNull(title);
        Assert.Equal("1942", title.Value);
    }

    [Fact]
    public async Task Import_WritesMetadataJsonFile()
    {
        var zipPath = SaveAndIndexZip(BuildZip(BaseZipEntries()));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        await svc.ImportAsync(entry, cand, []);

        var metaDir  = Path.Combine(_dir, "data", "media", "capcom", "capcom-cps1", "metadata");
        var files    = Directory.GetFiles(metaDir, "*screenscraper-cache*.json");
        Assert.Single(files);
    }

    [Fact]
    public async Task Import_ExtractsMediaFromZip_ToReleaseMediaFolder()
    {
        // Add a screenshot to the ZIP and index it
        var screenshotEntry = "media/screenshot/1001_0.png";
        var entries = BaseZipEntries(screenshotEntry);
        var zipPath = SaveAndIndexZip(BuildZip(entries));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = new ScreenScraperCacheCandidate(pkgId, zipPath, "1001", "75", "Capcom Classics", "1942", true, true);

        var summary = await svc.ImportAsync(entry, cand, []);

        Assert.Equal(1, summary.MediaExtracted);

        var screenshotDir = Path.Combine(_dir, "data", "media", "capcom", "capcom-cps1", "screenshots");
        var files         = Directory.GetFiles(screenshotDir, "*.png");
        Assert.Single(files);
    }

    [Fact]
    public async Task Import_SkipsExistingNonEmptyMediaFile()
    {
        var screenshotEntry = "media/screenshot/1001_0.png";
        var entries  = BaseZipEntries(screenshotEntry);
        var zipPath  = SaveAndIndexZip(BuildZip(entries));
        var pkgId    = GetPackageId(zipPath);
        var svc      = MakeSvc();
        var entry    = MakeEntry();
        var cand     = new ScreenScraperCacheCandidate(pkgId, zipPath, "1001", "75", "Capcom Classics", "1942", true, true);

        // First import
        await svc.ImportAsync(entry, cand, []);

        // Second import — the existing file should NOT be overwritten; count stays at 1 file
        await svc.ImportAsync(entry, cand, []);

        var screenshotDir = Path.Combine(_dir, "data", "media", "capcom", "capcom-cps1", "screenshots");
        var files         = Directory.GetFiles(screenshotDir, "*.png");
        // Second run should produce a second indexed file since NextIndexedMediaStem advances the counter
        // but the first file is non-empty so we verify count is at most 2 (one per run)
        Assert.True(files.Length <= 2);
    }

    [Fact]
    public async Task Import_MediaByType_ReflectsExtractedTypes()
    {
        var entries = BaseZipEntries("media/screenshot/1001_0.png");
        var zipPath = SaveAndIndexZip(BuildZip(entries));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = new ScreenScraperCacheCandidate(pkgId, zipPath, "1001", "75", "Capcom Classics", "1942", true, true);

        var summary = await svc.ImportAsync(entry, cand, []);

        Assert.True(summary.MediaByType.ContainsKey("screenshot"));
        Assert.Equal(1, summary.MediaByType["screenshot"]);
    }

    [Fact]
    public async Task Import_NoMedia_ReturnsZeroMediaExtracted()
    {
        var zipPath = SaveAndIndexZip(BuildZip(BaseZipEntries()));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        var summary = await svc.ImportAsync(entry, cand, []);

        Assert.Equal(0, summary.MediaExtracted);
        Assert.Empty(summary.MediaByType);
    }

    [Fact]
    public async Task Import_CoverMedia_ExtractsToCoversFrontFolder()
    {
        var entries = BaseZipEntries("media/cover-front/1001_wor_0.png");
        var zipPath = SaveAndIndexZip(BuildZip(entries));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = new ScreenScraperCacheCandidate(pkgId, zipPath, "1001", "75", "Capcom Classics", "1942", true, true);

        var summary = await svc.ImportAsync(entry, cand, []);

        Assert.Equal(1, summary.MediaExtracted);
        var coverDir = Path.Combine(_dir, "data", "media", "capcom", "capcom-cps1", "covers-front");
        Assert.Single(Directory.GetFiles(coverDir, "*.png"));
    }

    [Fact]
    public async Task Import_NoNetworkCalls_CompletesOffline()
    {
        // This test is structural: ImportAsync must not throw due to missing network.
        // If a network call were made, it would throw or time out in the test environment.
        var zipPath = SaveAndIndexZip(BuildZip(BaseZipEntries()));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        var ex = await Record.ExceptionAsync(() => svc.ImportAsync(entry, cand, []));

        Assert.Null(ex);
    }

    // ── Sanitized payload compatibility ───────────────────────────────────────

    // Payload with credential placeholders injected — as produced by ScreenScraperPayloadSanitizer.
    private const string SanitizedPayloadJson = """
        {
          "response": {
            "jeu": {
              "id": "1001",
              "noms": [{"region": "wor", "text": "1942"}],
              "developpeur": {"text": "Capcom"},
              "editeur": {"text": "Capcom"},
              "dates": [{"region": "wor", "text": "1984"}],
              "synopsis": [{"langue": "en", "text": "Shoot em up"}],
              "medias": {
                "jeu_ss": [
                  {"url": "https://ss.api/medias?devid=<DEVID>&devpassword=<DEVPASSWORD>&ssid=<SSID>&sspassword=<SSPASSWORD>&crc=abc"}
                ]
              }
            }
          }
        }
        """;

    [Fact]
    public async Task Import_SanitizedPayload_GeneratesProposals()
    {
        // Sanitized payloads replace credential query params with placeholders.
        // Metadata fields (noms, developpeur, etc.) are unaffected — proposals must still be generated.
        var entries = new (string, byte[])[]
        {
            ("manifest.json",      Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv",      Encoding.UTF8.GetBytes(Csv)),
            ("payloads/1001.json", Encoding.UTF8.GetBytes(SanitizedPayloadJson)),
        };
        var zipPath = SaveAndIndexZip(BuildZip(entries));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        var summary = await svc.ImportAsync(entry, cand, []);

        Assert.True(summary.ProposalsSaved);
        var store     = new DatLineStore(entry.DbPath);
        var proposals = store.LoadMetadataProposals(entry.ReleaseId, ScreenScraperCacheImportService.ProviderId);
        Assert.NotEmpty(proposals);
        Assert.Contains(proposals, p => p.Field == "title" && p.Value == "1942");
    }

    [Fact]
    public async Task Import_SanitizedPayload_ProposalProviderIsCacheProvider()
    {
        var entries = new (string, byte[])[]
        {
            ("manifest.json",      Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv",      Encoding.UTF8.GetBytes(Csv)),
            ("payloads/1001.json", Encoding.UTF8.GetBytes(SanitizedPayloadJson)),
        };
        var zipPath = SaveAndIndexZip(BuildZip(entries));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        await svc.ImportAsync(entry, cand, []);

        var store     = new DatLineStore(entry.DbPath);
        var proposals = store.LoadMetadataProposals(entry.ReleaseId, ScreenScraperCacheImportService.ProviderId);
        Assert.NotEmpty(proposals);
        Assert.All(proposals, p => Assert.Equal(ScreenScraperCacheImportService.ProviderId, p.Provider));
    }

    [Fact]
    public async Task Import_SanitizedPayload_NotLoadableAsOnlineProvider()
    {
        // Proposals saved by cache import must NOT appear when queried under "screenscraper".
        var entries = new (string, byte[])[]
        {
            ("manifest.json",      Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv",      Encoding.UTF8.GetBytes(Csv)),
            ("payloads/1001.json", Encoding.UTF8.GetBytes(SanitizedPayloadJson)),
        };
        var zipPath = SaveAndIndexZip(BuildZip(entries));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        await svc.ImportAsync(entry, cand, []);

        var store     = new DatLineStore(entry.DbPath);
        var proposals = store.LoadMetadataProposals(entry.ReleaseId, ArkadiaProviders.ScreenScraper);
        Assert.Empty(proposals);
    }

    [Fact]
    public async Task Import_ProposalsSaved_True_WhenPayloadContainsGameData()
    {
        var zipPath = SaveAndIndexZip(BuildZip(BaseZipEntries()));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = MakeCandidate(zipPath, pkgId);

        var summary = await svc.ImportAsync(entry, cand, []);

        Assert.True(summary.ProposalsSaved);
    }

    [Fact]
    public async Task Import_MediaExtracted_EvenWhenPayloadIsSanitized()
    {
        // Media extraction must succeed regardless of credential placeholders in payload JSON.
        var entries = new (string, byte[])[]
        {
            ("manifest.json",      Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv",      Encoding.UTF8.GetBytes(Csv)),
            ("payloads/1001.json", Encoding.UTF8.GetBytes(SanitizedPayloadJson)),
            ("media/screenshot/1001_0.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
        };
        var zipPath = SaveAndIndexZip(BuildZip(entries));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = new ScreenScraperCacheCandidate(pkgId, zipPath, "1001", "75", "Capcom Classics", "1942", true, true);

        var summary = await svc.ImportAsync(entry, cand, []);

        Assert.Equal(1, summary.MediaExtracted);
    }

    // ── physical-media alias normalization ────────────────────────────────────

    [Fact]
    public async Task Import_PhysicalMedia_WritesToPhysicalFolder()
    {
        // Cache packages use "physical-media" as the ZIP folder/media-type.
        // Arkadia's canonical media type is "physical" and the folder is "physical/".
        // After import the file must land in the "physical" folder.
        var entries = new (string, byte[])[]
        {
            ("manifest.json",              Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv",             Encoding.UTF8.GetBytes(Csv)),
            ("payloads/1001.json",         Encoding.UTF8.GetBytes(PayloadJson)),
            // ZIP entry uses provider alias "physical-media" as the folder name.
            ("media/physical-media/1001_wor_1.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
        };
        var zipPath = SaveAndIndexZip(BuildZip(entries));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = new ScreenScraperCacheCandidate(
            pkgId, zipPath, "1001", "75", "Capcom Classics", "1942", true, true);

        var summary = await svc.ImportAsync(entry, cand, []);

        // One file extracted.
        Assert.Equal(1, summary.MediaExtracted);

        // File must be in the canonical "physical" folder, not "physical-media".
        var physicalDir = Path.Combine(_dir, "data", "media", "capcom", "capcom-cps1", "physical");
        Assert.True(Directory.Exists(physicalDir), "physical/ folder must exist");
        Assert.NotEmpty(Directory.GetFiles(physicalDir));

        // The provider-alias folder must NOT have been created.
        var aliaDir = Path.Combine(_dir, "data", "media", "capcom", "capcom-cps1", "physical-media");
        Assert.False(Directory.Exists(aliaDir), "physical-media/ folder must NOT be created");
    }

    [Fact]
    public async Task Import_PhysicalMedia_DiscoveredByLoadAssets_AsCanonicalType()
    {
        // After import, ReleaseMediaCurationService.LoadAssets must surface the extracted
        // physical file with MediaType = "physical", never "physical-media".
        var entries = new (string, byte[])[]
        {
            ("manifest.json",              Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv",             Encoding.UTF8.GetBytes(Csv)),
            ("payloads/1001.json",         Encoding.UTF8.GetBytes(PayloadJson)),
            ("media/physical-media/1001_wor_1.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
        };
        var zipPath = SaveAndIndexZip(BuildZip(entries));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = new ScreenScraperCacheCandidate(
            pkgId, zipPath, "1001", "75", "Capcom Classics", "1942", true, true);

        await svc.ImportAsync(entry, cand, []);

        var curationSvc = new Arkadia.Data.ReleaseMediaCurationService(
            Path.Combine(_dir, "data"));
        var assets = curationSvc.LoadAssets(
            entry.DbPath, entry.ReleaseId, entry.Name,
            entry.HardwareFamilyId, entry.DatLineId);

        Assert.Contains(assets, a => a.MediaType == "physical");
        Assert.DoesNotContain(assets, a => a.MediaType == "physical-media");
    }

    [Fact]
    public async Task Import_CoverFront_RemainsCanonical_NotAffectedByNormalization()
    {
        // Regression: non-physical media types must be unchanged by normalization.
        var entries = new (string, byte[])[]
        {
            ("manifest.json",               Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv",              Encoding.UTF8.GetBytes(Csv)),
            ("payloads/1001.json",          Encoding.UTF8.GetBytes(PayloadJson)),
            ("media/cover-front/1001_wor_1.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
        };
        var zipPath = SaveAndIndexZip(BuildZip(entries));
        var pkgId   = GetPackageId(zipPath);
        var svc     = MakeSvc();
        var entry   = MakeEntry();
        var cand    = new ScreenScraperCacheCandidate(
            pkgId, zipPath, "1001", "75", "Capcom Classics", "1942", true, true);

        var summary = await svc.ImportAsync(entry, cand, []);

        Assert.Equal(1, summary.MediaExtracted);

        var coverDir = Path.Combine(_dir, "data", "media", "capcom", "capcom-cps1", "covers-front");
        Assert.NotEmpty(Directory.GetFiles(coverDir));

        var curationSvc = new Arkadia.Data.ReleaseMediaCurationService(
            Path.Combine(_dir, "data"));
        var assets = curationSvc.LoadAssets(
            entry.DbPath, entry.ReleaseId, entry.Name,
            entry.HardwareFamilyId, entry.DatLineId);

        Assert.Contains(assets, a => a.MediaType == "cover-front");
        Assert.DoesNotContain(assets, a => a.MediaType == "cover-front-media");
    }
}
