using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Arkadia;
using Arkadia.Data;
using Arkadia.Library;
using Arkadia.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests;

public sealed class CatalogBulkScrapeServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly CatalogService _catalog;
    private readonly ScreenScraperCachePackageImporter _importer;

    // Minimal valid payload for game 1001 titled "1942"
    private const string Payload1001 = """
        {
          "response": {
            "jeu": {
              "id": "1001",
              "noms": [{"region": "wor", "text": "1942"}],
              "developpeur": {"text": "Capcom"},
              "editeur": {"text": "Capcom"},
              "dates": [{"region": "wor", "text": "1984"}],
              "langues": [{"shortname": "en"}],
              "synopsis": [{"langue": "en", "text": "Shoot em up"}]
            }
          }
        }
        """;

    // Minimal valid payload for game 2001 titled "Galaga"
    private const string Payload2001 = """
        {
          "response": {
            "jeu": {
              "id": "2001",
              "noms": [{"region": "wor", "text": "Galaga"}],
              "developpeur": {"text": "Namco"},
              "editeur": {"text": "Namco"},
              "dates": [{"region": "wor", "text": "1981"}],
              "langues": [{"shortname": "en"}],
              "synopsis": [{"langue": "en", "text": "Classic arcade"}]
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
            "systemName": "Capcom",
            "builtAtUtc": "2026-05-01T00:00:00Z",
            "gameCount": 2
        }
        """;

    public CatalogBulkScrapeServiceTests()
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

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private string IndexZip(
        string gameId = "1001",
        string gameTitle = "1942",
        string payload = Payload1001,
        string? mediaZipEntry = null,
        string? zipName = null)
    {
        var csv = $"""
            "Game ID";"Game Name"
            "{gameId}";"{gameTitle}"
            """;

        var entries = new List<(string, byte[])>
        {
            ("manifest.json",                Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv",               Encoding.UTF8.GetBytes(csv)),
            ($"payloads/{gameId}.json",     Encoding.UTF8.GetBytes(payload)),
        };
        if (mediaZipEntry is not null)
            entries.Add((mediaZipEntry, PngBytes));

        var path = Path.Combine(_dir, zipName ?? $"pkg_{gameId}.zip");
        File.WriteAllBytes(path, BuildZip(entries.ToArray()));
        _importer.IndexPackage(path);
        return path;
    }

    private LibraryEntry MakeEntry(
        string releaseId      = "rel-001",
        string name           = "1942 (World)",
        string catalogTitle   = "1942",
        string hwFamilyId     = "capcom",
        string datLineId      = "capcom-cps1",
        ReleaseMetadataRecord? metadata = null)
    {
        var dbDir = Path.Combine(_dir, "data", "systems", hwFamilyId);
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, $"{datLineId}.db");
        return new LibraryEntry
        {
            Name             = name,
            CatalogTitle     = catalogTitle,
            Platform         = "Arcade",
            HardwareFamilyId = hwFamilyId,
            DatLineId        = datLineId,
            Status           = "Present",
            Region           = "wor",
            Languages        = "en",
            Format           = "rom",
            Size             = "64 KB",
            Tier             = "A",
            ReleaseId        = releaseId,
            DbPath           = dbPath,
            Metadata         = metadata,
        };
    }

    private CatalogBulkScrapeService MakeSvc()
    {
        var importSvc = new ScreenScraperCacheImportService(
            Path.Combine(_dir, "data"), _catalog);
        return new CatalogBulkScrapeService(
            Path.Combine(_dir, "data"), _catalog, importSvc);
    }

    private static BulkScrapeOptions DefaultOptions() => new(
        Scope:                  BulkScrapeScope.MissingOnly,
        AutoApplyEmptyFieldsOnly: true,
        ExtractMissingMedia:      true,
        RespectExcludedMedia:     true,
        OverwriteExistingMedia:   false);

    // ── 1. Empty list ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_EmptyList_ReturnsEmptyReport()
    {
        var svc    = MakeSvc();
        var report = await svc.RunAsync([], DefaultOptions(), []);

        Assert.Empty(report.Results);
        Assert.Equal(0, report.TotalMatched);
        Assert.Equal(0, report.TotalNoMatch);
        Assert.Equal(0, report.TotalAmbiguous);
        Assert.Equal(0, report.TotalErrors);
        Assert.Equal(0, report.TotalMetadataApplied);
        Assert.Equal(0, report.TotalMediaExtracted);
    }

    // ── 2. NoMatch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoMatch_WhenNoCandidatesFound()
    {
        // No ZIP indexed — search returns nothing
        var svc    = MakeSvc();
        var entry  = MakeEntry(catalogTitle: "UnknownGame");
        var report = await svc.RunAsync([entry], DefaultOptions(), []);

        Assert.Single(report.Results);
        Assert.Equal(BulkScrapeStatus.NoMatch, report.Results[0].Status);
        Assert.Equal(1, report.TotalNoMatch);
        Assert.Equal(0, report.TotalMatched);
    }

    // ── 3. Ambiguous ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Ambiguous_WhenMultipleCandidatesFound()
    {
        // Index two different ZIPs with titles that both contain "1942"
        IndexZip("1001", "1942",           Payload1001, zipName: "pkg1.zip");
        IndexZip("1002", "1942 (Revision)", Payload1001, zipName: "pkg2.zip");

        var svc    = MakeSvc();
        var entry  = MakeEntry(catalogTitle: "1942");
        var report = await svc.RunAsync([entry], DefaultOptions(), []);

        Assert.Single(report.Results);
        Assert.Equal(BulkScrapeStatus.Ambiguous, report.Results[0].Status);
        Assert.Equal(1, report.TotalAmbiguous);
    }

    // ── 3b. Ambiguous — no DB side effects ───────────────────────────────────

    [Fact]
    public async Task RunAsync_Ambiguous_DoesNotApplyOrSaveAnyData()
    {
        // Two ZIPs both matching "1942" triggers Ambiguous.
        IndexZip("1001", "1942",           Payload1001, zipName: "pkg1.zip");
        IndexZip("1002", "1942 (Revision)", Payload1001, zipName: "pkg2.zip");

        var svc   = MakeSvc();
        var entry = MakeEntry(catalogTitle: "1942");
        await svc.RunAsync([entry], DefaultOptions(), []);

        // No proposals or metadata must have been written.
        var store     = new DatLineStore(entry.DbPath);
        var proposals = store.LoadMetadataProposals(entry.ReleaseId, ScreenScraperCacheImportService.ProviderId);
        Assert.Empty(proposals);

        var metadata = store.LoadReleaseMetadata().GetValueOrDefault(entry.ReleaseId);
        Assert.Null(metadata);
    }

    // ── 4. Matched — single candidate ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Matched_WhenSingleCandidateFound()
    {
        IndexZip();
        var svc    = MakeSvc();
        var entry  = MakeEntry();
        var report = await svc.RunAsync([entry], DefaultOptions(), []);

        Assert.Single(report.Results);
        Assert.Equal(BulkScrapeStatus.Matched, report.Results[0].Status);
        Assert.Equal(1, report.TotalMatched);
    }

    // ── 5. Metadata auto-applied to empty fields ───────────────────────────────

    [Fact]
    public async Task RunAsync_MetadataApplied_WithAutoApplyEmptyFields()
    {
        IndexZip();
        var svc    = MakeSvc();
        var entry  = MakeEntry();
        var opts   = DefaultOptions() with { AutoApplyEmptyFieldsOnly = true };
        var report = await svc.RunAsync([entry], opts, []);

        Assert.Equal(BulkScrapeStatus.Matched, report.Results[0].Status);
        Assert.True(report.Results[0].MetadataFieldsApplied > 0);
        Assert.True(report.TotalMetadataApplied > 0);

        // Metadata should be saved in the DatLine DB
        var store    = new DatLineStore(entry.DbPath);
        var metadata = store.LoadReleaseMetadata().GetValueOrDefault(entry.ReleaseId);
        Assert.NotNull(metadata);
        Assert.Equal("1942", metadata.Title);
    }

    // ── 6. AutoApply=false → proposals only ───────────────────────────────────

    [Fact]
    public async Task RunAsync_Proposals_WhenAutoApplyFalse()
    {
        IndexZip();
        var svc    = MakeSvc();
        var entry  = MakeEntry();
        var opts   = DefaultOptions() with { AutoApplyEmptyFieldsOnly = false };
        var report = await svc.RunAsync([entry], opts, []);

        Assert.Equal(BulkScrapeStatus.Matched, report.Results[0].Status);
        Assert.Equal(0, report.Results[0].MetadataFieldsApplied);

        // Proposals should be saved but not applied
        var store     = new DatLineStore(entry.DbPath);
        var proposals = store.LoadMetadataProposals(entry.ReleaseId, ScreenScraperCacheImportService.ProviderId);
        Assert.NotEmpty(proposals);
        // But no release_metadata row saved (auto-applied count = 0)
        var metadata = store.LoadReleaseMetadata().GetValueOrDefault(entry.ReleaseId);
        Assert.Null(metadata);
    }

    // ── 7. Media extracted ────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MediaExtracted_WhenMediaInZip()
    {
        // Media zip entry: media/cover-front/1001_wor_001.png
        IndexZip(mediaZipEntry: "media/cover-front/1001_wor_001.png");
        var svc    = MakeSvc();
        var entry  = MakeEntry();
        var report = await svc.RunAsync([entry], DefaultOptions(), []);

        Assert.Equal(BulkScrapeStatus.Matched, report.Results[0].Status);
        Assert.Equal(1, report.Results[0].MediaExtracted);
        Assert.Equal(1, report.TotalMediaExtracted);
    }

    // ── 8. ExtractMissingMedia=false → no media ────────────────────────────────

    [Fact]
    public async Task RunAsync_NoMedia_WhenExtractMediaFalse()
    {
        IndexZip(mediaZipEntry: "media/cover-front/1001_wor_001.png");
        var svc    = MakeSvc();
        var entry  = MakeEntry();
        var opts   = DefaultOptions() with { ExtractMissingMedia = false };
        var report = await svc.RunAsync([entry], opts, []);

        Assert.Equal(BulkScrapeStatus.Matched, report.Results[0].Status);
        Assert.Equal(0, report.Results[0].MediaExtracted);
    }

    // ── 9. Error when payload missing ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Error_WhenPayloadMissingFromZip()
    {
        // Index zip without the payload entry but catalog still has has_payload=1
        // We index it with payload, then delete the zip content and re-save without payload
        var csv = """
            "Game ID";"Game Name"
            "9999";"Orphan Game"
            """;
        var entries = new (string, byte[])[]
        {
            ("manifest.json",     Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv",     Encoding.UTF8.GetBytes(csv)),
            ("payloads/9999.json", Encoding.UTF8.GetBytes(Payload1001)),
        };
        var zipPath = Path.Combine(_dir, "pkg_orphan.zip");
        File.WriteAllBytes(zipPath, BuildZip(entries));
        _importer.IndexPackage(zipPath);

        // Now recreate the zip WITHOUT the payload so batch import fails
        var noPayloadEntries = new (string, byte[])[]
        {
            ("manifest.json", Encoding.UTF8.GetBytes(Manifest)),
            ("gameslist.csv", Encoding.UTF8.GetBytes(csv)),
        };
        File.WriteAllBytes(zipPath, BuildZip(noPayloadEntries));

        var svc    = MakeSvc();
        var entry  = MakeEntry(releaseId: "rel-orphan", catalogTitle: "Orphan Game");
        var report = await svc.RunAsync([entry], DefaultOptions(), []);

        Assert.Equal(BulkScrapeStatus.Error, report.Results[0].Status);
        Assert.NotNull(report.Results[0].ErrorMessage);
        Assert.Equal(1, report.TotalErrors);
    }

    // ── 10. Error does not abort remaining entries ─────────────────────────────

    [Fact]
    public async Task RunAsync_Error_DoesNotAbortRemainingEntries()
    {
        // Entry 1 will error (no package), entry 2 will match
        IndexZip("2001", "Galaga", Payload2001, zipName: "galaga.zip");

        var entryError = MakeEntry("rel-none", "UnknownXXX", "UnknownXXX");
        var entryOk    = MakeEntry("rel-galaga", "Galaga (World)", "Galaga",
                             hwFamilyId: "namco", datLineId: "namco-classic");

        var svc    = MakeSvc();
        var report = await svc.RunAsync([entryError, entryOk], DefaultOptions(), []);

        Assert.Equal(2, report.Results.Count);
        Assert.Equal(BulkScrapeStatus.NoMatch, report.Results[0].Status);
        Assert.Equal(BulkScrapeStatus.Matched,  report.Results[1].Status);
    }

    // ── 11. Cancellation — pre-cancelled ─────────────────────────────────────

    [Fact]
    public async Task RunAsync_Cancellation_ThrowsOperationCanceled()
    {
        IndexZip();
        var svc = MakeSvc();
        var entries = Enumerable.Range(0, 5)
            .Select(i => MakeEntry($"rel-{i:000}", $"1942 (Rev {i})", "1942"))
            .ToList();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.RunAsync(entries, DefaultOptions(), [], ct: cts.Token));
    }

    // ── 11b. Cancellation — mid-loop after first entry ────────────────────────

    [Fact]
    public async Task RunAsync_Cancellation_MidLoop_StopsAfterFirstEntry()
    {
        // Both entries match the same ZIP so entry 2 would also call BatchImportAsync,
        // where ct.ThrowIfCancellationRequested() fires after the token is cancelled.
        IndexZip("1001", "1942", Payload1001);

        var svc = MakeSvc();
        var entries = new List<LibraryEntry>
        {
            MakeEntry("rel-001", "1942 (World)", "1942", "capcom", "cps1"),
            MakeEntry("rel-002", "1942 (Japan)", "1942", "capcom", "cps2"),
        };

        using var cts = new CancellationTokenSource();

        // Cancel synchronously after the first entry's completion is reflected in progress.
        // RunAsync calls progress.Report(processed=1,...) at the start of entry 2's iteration,
        // then BatchImportAsync checks ct and throws.
        var progress = new SyncProgress<BulkScrapeProgress>(p =>
        {
            if (p.Processed >= 1) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.RunAsync(entries, DefaultOptions(), [], progress, cts.Token));

        // Entry 1's side effects must be visible — proposals were committed before cancellation.
        var store1    = new DatLineStore(entries[0].DbPath);
        var proposals = store1.LoadMetadataProposals(
            "rel-001", ScreenScraperCacheImportService.ProviderId);
        Assert.NotEmpty(proposals);

        // Entry 2 was not fully processed — no curation or proposal rows.
        var store2      = new DatLineStore(entries[1].DbPath);
        var proposals2  = store2.LoadMetadataProposals(
            "rel-002", ScreenScraperCacheImportService.ProviderId);
        Assert.Empty(proposals2);
    }

    // Synchronous IProgress<T> for deterministic mid-loop cancellation tests.
    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    // ── 12. Report totals match per-result counts ──────────────────────────────

    [Fact]
    public async Task RunAsync_ReportTotals_MatchPerResultCounts()
    {
        IndexZip("1001", "1942",   Payload1001, zipName: "pkg1.zip");
        IndexZip("2001", "Galaga", Payload2001, zipName: "pkg2.zip");

        var svc = MakeSvc();
        var entries = new List<LibraryEntry>
        {
            MakeEntry("rel-001", "1942 (World)",   "1942",   "capcom", "cps1"),
            MakeEntry("rel-002", "Galaga (World)", "Galaga",  "namco",  "ng"),
            MakeEntry("rel-003", "Unknown Game",   "Zzzzzz",  "other",  "ot"),
        };

        var report = await svc.RunAsync(entries, DefaultOptions(), []);

        int sumMatched   = report.Results.Count(r => r.Status == BulkScrapeStatus.Matched);
        int sumNoMatch   = report.Results.Count(r => r.Status == BulkScrapeStatus.NoMatch);
        int sumAmbiguous = report.Results.Count(r => r.Status == BulkScrapeStatus.Ambiguous);
        int sumErrors    = report.Results.Count(r => r.Status == BulkScrapeStatus.Error);
        int sumMeta      = report.Results.Sum(r => r.MetadataFieldsApplied);
        int sumMedia     = report.Results.Sum(r => r.MediaExtracted);

        Assert.Equal(sumMatched,   report.TotalMatched);
        Assert.Equal(sumNoMatch,   report.TotalNoMatch);
        Assert.Equal(sumAmbiguous, report.TotalAmbiguous);
        Assert.Equal(sumErrors,    report.TotalErrors);
        Assert.Equal(sumMeta,      report.TotalMetadataApplied);
        Assert.Equal(sumMedia,     report.TotalMediaExtracted);
    }

    // ── 13. Progress is reported for each entry ────────────────────────────────

    [Fact]
    public async Task RunAsync_ProgressReported_ForEachEntry()
    {
        IndexZip();
        var svc     = MakeSvc();
        var entries = new List<LibraryEntry>
        {
            MakeEntry("rel-001", "1942 (World)", "1942", "capcom", "cps1"),
            MakeEntry("rel-002", "1942 (Japan)", "1942", "capcom", "cps2"),
        };

        var reports = new List<BulkScrapeProgress>();
        var progress = new Progress<BulkScrapeProgress>(p => reports.Add(p));

        await svc.RunAsync(entries, DefaultOptions(), [], progress);

        // At least one progress event per entry + final
        Assert.True(reports.Count >= entries.Count);
        Assert.Equal(2, reports.Last().Processed);
    }

    // ── 14. Extra notes not altered ───────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ExtraNotes_NotAltered()
    {
        IndexZip();
        var svc   = MakeSvc();
        var entry = MakeEntry();

        // Seed extra notes before bulk scrape
        var store = new DatLineStore(entry.DbPath);
        store.SaveReleaseExtraNotes(entry.ReleaseId, "My curated notes");

        await svc.RunAsync([entry], DefaultOptions(), []);

        Assert.Equal("My curated notes", store.GetReleaseExtraNotes(entry.ReleaseId));
    }

    // ── 15. Media credits not altered ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MediaCredits_NotAltered()
    {
        IndexZip(mediaZipEntry: "media/cover-front/1001_wor_001.png");
        var svc   = MakeSvc();
        var entry = MakeEntry();

        // Seed a curation row with credits
        var store = new DatLineStore(entry.DbPath);
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      entry.ReleaseId,
            MediaType:      "cover-front",
            FilePath:       "/some/path/cover.png",
            FileSha256:     null,
            IsPreferred:    false,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        "Artist Name",
            Notes:          null));

        await svc.RunAsync([entry], DefaultOptions(), []);

        var rows = store.LoadMediaCurationRows(entry.ReleaseId);
        var creditsRow = rows.FirstOrDefault(r => r.Credits == "Artist Name");
        Assert.NotNull(creditsRow);
    }

    // ── 16. Preferred flag not altered ────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PreferredFlag_NotAltered()
    {
        IndexZip();
        var svc   = MakeSvc();
        var entry = MakeEntry();

        // Seed a preferred row
        var store = new DatLineStore(entry.DbPath);
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      entry.ReleaseId,
            MediaType:      "cover-front",
            FilePath:       "/some/path/preferred.png",
            FileSha256:     null,
            IsPreferred:    true,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        await svc.RunAsync([entry], DefaultOptions(), []);

        var rows = store.LoadMediaCurationRows(entry.ReleaseId);
        var preferred = rows.FirstOrDefault(r => r.IsPreferred);
        Assert.NotNull(preferred);
        Assert.Equal("/some/path/preferred.png", preferred.FilePath);
    }

    // ── 17. RespectExcludedHashes — file deleted when hash matches ─────────────

    [Fact]
    public async Task RunAsync_RespectExcludedHashes_DeletesReintroducedFile()
    {
        IndexZip(mediaZipEntry: "media/cover-front/1001_wor_001.png");

        var svc   = MakeSvc();
        var entry = MakeEntry();

        // Compute what SHA-256 the PNG bytes will produce
        var pngTmp = Path.Combine(_dir, "test.png");
        File.WriteAllBytes(pngTmp, PngBytes);
        var sha256 = ReleaseMediaCurationService.ComputeSha256(pngTmp)!;

        // Mark that hash as excluded
        var store = new DatLineStore(entry.DbPath);
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      entry.ReleaseId,
            MediaType:      "cover-front",
            FilePath:       "/old/deleted.png",
            FileSha256:     sha256,
            IsPreferred:    false,
            IsExcluded:     true,
            ExcludedReason: "unwanted",
            Credits:        null,
            Notes:          null));

        var opts   = DefaultOptions() with { RespectExcludedMedia = true };
        var report = await svc.RunAsync([entry], opts, []);

        Assert.Equal(BulkScrapeStatus.Matched, report.Results[0].Status);
        Assert.Equal(0, report.Results[0].MediaExtracted);

        // No cover-front file should be on disk
        var mediaRoot = Path.Combine(_dir, "data", "media", "capcom", "capcom-cps1", "covers-front");
        var files = Directory.Exists(mediaRoot)
            ? Directory.GetFiles(mediaRoot)
            : [];
        Assert.Empty(files);
    }

    // ── 18. RespectExcluded=false — file kept even if hash matches ─────────────

    [Fact]
    public async Task RunAsync_IgnoreExcludedHashes_WhenRespectFalse()
    {
        IndexZip(mediaZipEntry: "media/cover-front/1001_wor_001.png");

        var svc   = MakeSvc();
        var entry = MakeEntry();

        var pngTmp = Path.Combine(_dir, "test.png");
        File.WriteAllBytes(pngTmp, PngBytes);
        var sha256 = ReleaseMediaCurationService.ComputeSha256(pngTmp)!;

        var store = new DatLineStore(entry.DbPath);
        store.UpsertMediaCurationRow(new MediaCurationRow(
            entry.ReleaseId, "cover-front", "/old/deleted.png",
            sha256, false, true, "unwanted", null, null));

        var opts   = DefaultOptions() with { RespectExcludedMedia = false };
        var report = await svc.RunAsync([entry], opts, []);

        Assert.Equal(1, report.Results[0].MediaExtracted);
    }

    // ── 19. Multiple entries — all processed ──────────────────────────────────

    [Fact]
    public async Task RunAsync_MultipleEntries_AllProcessed()
    {
        IndexZip("1001", "1942",   Payload1001, zipName: "pkg1.zip");
        IndexZip("2001", "Galaga", Payload2001, zipName: "pkg2.zip");

        var svc = MakeSvc();
        var entries = new List<LibraryEntry>
        {
            MakeEntry("rel-001", "1942 (World)",   "1942",   "capcom", "cps1"),
            MakeEntry("rel-002", "Galaga (World)", "Galaga",  "namco",  "ng"),
        };

        var report = await svc.RunAsync(entries, DefaultOptions(), []);

        Assert.Equal(2, report.Results.Count);
        Assert.All(report.Results, r => Assert.Equal(BulkScrapeStatus.Matched, r.Status));
        Assert.Equal(2, report.TotalMatched);
    }

    // ── 20. Mixed results — all statuses in one run ────────────────────────────

    [Fact]
    public async Task RunAsync_MixedResults_AllStatusesPresent()
    {
        IndexZip("1001", "1942",            Payload1001, zipName: "pkg1.zip");
        IndexZip("1002", "1942 (Revision)", Payload1001, zipName: "pkg2.zip");

        var svc = MakeSvc();
        var entries = new List<LibraryEntry>
        {
            MakeEntry("rel-001", "1942 (World)", "1942",    "capcom", "cps1"),  // Ambiguous (2 matches)
            MakeEntry("rel-002", "Unknown XXX",  "Unknown", "capcom", "cps2"),  // NoMatch
        };

        var report = await svc.RunAsync(entries, DefaultOptions(), []);

        Assert.Equal(2, report.Results.Count);
        Assert.Contains(report.Results, r => r.Status == BulkScrapeStatus.Ambiguous);
        Assert.Contains(report.Results, r => r.Status == BulkScrapeStatus.NoMatch);
    }

    // ── 21. IsComplete — false when quality score low ─────────────────────────

    [Fact]
    public void IsComplete_ReturnsFalse_WhenQualityScoreLow()
    {
        var svc   = MakeSvc();
        var entry = MakeEntry(metadata: new ReleaseMetadataRecord
        {
            ReleaseId = "rel-001",
            Title     = "1942",   // score = 1
        });
        Assert.False(svc.IsComplete(entry));
    }

    // ── 22. IsComplete — false when no cover-front ────────────────────────────

    [Fact]
    public void IsComplete_ReturnsFalse_WhenNoCoverFront()
    {
        var svc   = MakeSvc();
        var entry = MakeEntry(metadata: new ReleaseMetadataRecord
        {
            ReleaseId  = "rel-001",
            Title      = "1942",
            Developer  = "Capcom",
            Publisher  = "Capcom",
            Year       = "1984",
            Languages  = "en",
            OriginalTitle = "1942",  // score = 6
        });
        // No cover-front file on disk → IsComplete = false
        Assert.False(svc.IsComplete(entry));
    }

    // ── 23. IsComplete — true when score=6 and cover exists ───────────────────

    [Fact]
    public void IsComplete_ReturnsTrue_WhenScoreFullAndCoverExists()
    {
        var svc   = MakeSvc();
        var entry = MakeEntry(metadata: new ReleaseMetadataRecord
        {
            ReleaseId     = "rel-001",
            Title         = "1942",
            OriginalTitle = "1942",
            Developer     = "Capcom",
            Publisher     = "Capcom",
            Year          = "1984",
            Languages     = "en",
        });

        // Place a cover-front file on disk
        var coverDir = Path.Combine(
            _dir, "data", "media", entry.HardwareFamilyId, entry.DatLineId, "covers-front");
        Directory.CreateDirectory(coverDir);
        var stem = MediaStore.ReleaseStem(entry.Name) + "_wor_001.png";
        File.WriteAllBytes(Path.Combine(coverDir, stem), PngBytes);

        Assert.True(svc.IsComplete(entry));
    }

    // ── 24. IsComplete — false when metadata null ─────────────────────────────

    [Fact]
    public void IsComplete_ReturnsFalse_WhenMetadataNull()
    {
        var svc   = MakeSvc();
        var entry = MakeEntry(metadata: null);
        Assert.False(svc.IsComplete(entry));
    }

    // ── 26–30. FilterEntries scope logic ──────────────────────────────────────

    [Fact]
    public void FilterEntries_CurrentRelease_ReturnsOnlySelected()
    {
        var svc      = MakeSvc();
        var selected = MakeEntry("rel-001", "1942 (World)", "1942");
        var other    = MakeEntry("rel-002", "Galaga (World)", "Galaga", "namco", "ng");

        var result = svc.FilterEntries([selected, other], BulkScrapeScope.CurrentRelease, selected);

        var single = Assert.Single(result);
        Assert.Equal("rel-001", single.ReleaseId);
    }

    [Fact]
    public void FilterEntries_CurrentRelease_NullSelected_ReturnsEmpty()
    {
        var svc   = MakeSvc();
        var entry = MakeEntry();

        var result = svc.FilterEntries([entry], BulkScrapeScope.CurrentRelease, selectedEntry: null);

        Assert.Empty(result);
    }

    [Fact]
    public void FilterEntries_EntireDat_ReturnsAllEntries()
    {
        var svc = MakeSvc();
        var all = new LibraryEntry[]
        {
            MakeEntry("rel-001", "1942 (World)",   "1942",   "capcom", "cps1"),
            MakeEntry("rel-002", "Galaga (World)", "Galaga",  "namco",  "ng"),
            MakeEntry("rel-003", "Unknown",        "Unknown", "other",  "ot"),
        };

        var result = svc.FilterEntries(all, BulkScrapeScope.EntireDat, selectedEntry: null);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void FilterEntries_MissingOnly_ExcludesCompleteEntries()
    {
        var svc = MakeSvc();

        // Complete entry: metadata score=6 AND cover-front file on disk
        var complete = MakeEntry("rel-ok", "1942 (World)", "1942", metadata: new ReleaseMetadataRecord
        {
            ReleaseId     = "rel-ok",
            Title         = "1942",
            OriginalTitle = "1942",
            Developer     = "Capcom",
            Publisher     = "Capcom",
            Year          = "1984",
            Languages     = "en",
        });
        var coverDir = Path.Combine(
            _dir, "data", "media", complete.HardwareFamilyId, complete.DatLineId, "covers-front");
        Directory.CreateDirectory(coverDir);
        File.WriteAllBytes(
            Path.Combine(coverDir, MediaStore.ReleaseStem(complete.Name) + "_wor_001.png"),
            PngBytes);

        // Incomplete entry: no metadata
        var incomplete = MakeEntry("rel-miss", "Galaga (World)", "Galaga", "namco", "ng");

        var result = svc.FilterEntries([complete, incomplete], BulkScrapeScope.MissingOnly, selectedEntry: null);

        var single = Assert.Single(result);
        Assert.Equal("rel-miss", single.ReleaseId);
    }

    [Fact]
    public void FilterEntries_MissingOnly_PreservesInputOrder()
    {
        var svc = MakeSvc();
        var entries = new LibraryEntry[]
        {
            MakeEntry("rel-003", "Zebra",   "Zebra",   "a", "a1"),
            MakeEntry("rel-001", "Aardvark","Aardvark", "a", "a2"),
            MakeEntry("rel-002", "Monkey",  "Monkey",   "a", "a3"),
        };

        // All are incomplete (no metadata) — order must be preserved.
        var result = svc.FilterEntries(entries, BulkScrapeScope.MissingOnly, selectedEntry: null);

        Assert.Equal(3, result.Count);
        Assert.Equal("rel-003", result[0].ReleaseId);
        Assert.Equal("rel-001", result[1].ReleaseId);
        Assert.Equal("rel-002", result[2].ReleaseId);
    }

    // ── 25. All-matched report has correct metadata total ─────────────────────

    [Fact]
    public async Task RunAsync_AllMatched_MetadataTotalIsCorrect()
    {
        IndexZip("1001", "1942",   Payload1001, zipName: "pkg1.zip");
        IndexZip("2001", "Galaga", Payload2001, zipName: "pkg2.zip");

        var svc = MakeSvc();
        var entries = new List<LibraryEntry>
        {
            MakeEntry("rel-001", "1942 (World)",   "1942",   "capcom", "cps1"),
            MakeEntry("rel-002", "Galaga (World)", "Galaga",  "namco",  "ng"),
        };

        var report = await svc.RunAsync(entries, DefaultOptions(), []);

        Assert.Equal(2, report.TotalMatched);
        Assert.True(report.TotalMetadataApplied > 0);
        Assert.Equal(
            report.Results.Sum(r => r.MetadataFieldsApplied),
            report.TotalMetadataApplied);
    }
}
