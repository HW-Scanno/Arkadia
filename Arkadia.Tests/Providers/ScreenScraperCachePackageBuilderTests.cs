using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Providers;

// ── Media-aware fake fetcher ──────────────────────────────────────────────────

/// <summary>Returns a result with one screenshot and one cover-front per game.</summary>
internal sealed class MediaFakeDetailsFetcher : IScreenScraperDetailsFetcher
{
    public int FetchCount { get; private set; }

    public Task<ScreenScraperResult?> FetchAsync(string gameId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FetchCount++;
        return Task.FromResult<ScreenScraperResult?>(new ScreenScraperResult
        {
            RawJson             = $"{{\"id\":{gameId}}}",
            GameplayScreenshots = [new ScreenScraperMediaItem($"http://ss.example/ss_{gameId}.png",    "png", 100)],
            CoverFront          = [new ScreenScraperCoverItem("us", $"http://ss.example/box_{gameId}.jpg", "jpg", 200)],
        });
    }
}

// ── Fake media downloader ─────────────────────────────────────────────────────

/// <summary>
/// Writes a 3-byte dummy file (stem + first valid extension).
/// Supports rate-limit simulation (<paramref name="rateLimitAfterN"/>) and
/// generic failure simulation (<paramref name="throwAfterN"/>).
/// </summary>
internal sealed class FakeMediaDownloader(
    int rateLimitAfterN = int.MaxValue,
    int throwAfterN     = int.MaxValue) : IMediaDownloader
{
    public int DownloadCount { get; private set; }

    public Task<string?> DownloadAsync(
        string url, string destStem, string hintFormat,
        IReadOnlyList<string> validExts, long? expectedSize,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (DownloadCount >= rateLimitAfterN) throw new ScreenScraperRateLimitException();
        if (DownloadCount >= throwAfterN)     throw new IOException("simulated download failure");
        DownloadCount++;
        var ext  = validExts.Count > 0 ? (validExts[0].StartsWith('.') ? validExts[0] : "." + validExts[0]) : ".bin";
        var path = destStem + ext;
        File.WriteAllBytes(path, [1, 2, 3]);
        return Task.FromResult<string?>(path);
    }
}

// ── Fake fetchers ─────────────────────────────────────────────────────────────

internal sealed class FakeDetailsFetcher : IScreenScraperDetailsFetcher
{
    private readonly Dictionary<string, string?> _payloads;
    public int FetchCount { get; private set; }

    public FakeDetailsFetcher(Dictionary<string, string?> payloads) => _payloads = payloads;

    public Task<ScreenScraperResult?> FetchAsync(string gameId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FetchCount++;
        if (_payloads.TryGetValue(gameId, out var raw))
            return Task.FromResult(raw is null ? null : (ScreenScraperResult?)new ScreenScraperResult { RawJson = raw });
        return Task.FromResult<ScreenScraperResult?>(new ScreenScraperResult { RawJson = $"{{\"id\":{gameId}}}" });
    }
}

internal sealed class RateLimitFetcher : IScreenScraperDetailsFetcher
{
    private readonly int _throwAfterN;
    public int FetchCount { get; private set; }

    public RateLimitFetcher(int throwAfterN = 0) => _throwAfterN = throwAfterN;

    public Task<ScreenScraperResult?> FetchAsync(string gameId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FetchCount >= _throwAfterN)
            throw new ScreenScraperRateLimitException();
        FetchCount++;
        return Task.FromResult<ScreenScraperResult?>(new ScreenScraperResult { RawJson = "{}" });
    }
}

// ── Test class ────────────────────────────────────────────────────────────────

public sealed class ScreenScraperCachePackageBuilderTests : IDisposable
{
    private readonly string _dir;
    private readonly CatalogService _catalog;

    private string StagingRoot => Path.Combine(_dir, "staging-cache");
    private string OutputDir   => Path.Combine(_dir, "cache-screenscraper");

    private const string ValidCsv = """
        "Game ID";"Game Name"
        "39874";"1942"
        "39875";"1943 - The Battle Of Midway"
        """;

    public ScreenScraperCachePackageBuilderTests()
    {
        _dir     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _catalog = new CatalogService(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── Fixture helpers ───────────────────────────────────────────────────────

    private string WriteCsv(string content, string name = "games.csv")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private ScreenScraperCachePackageBuildOptions MakeOptions(
        string csvPath,
        string packageName     = "test-package",
        int    maxScrapes      = 19000,
        bool   force           = false,
        bool   updatePayloads  = false,
        bool   indexAfterBuild = false,
        bool   keepStaging     = true) => new(
            CsvPath:           csvPath,
            SystemId:          "75",
            SystemName:        "Capcom Classics",
            PackageName:       packageName,
            OutputZipPath:     Path.Combine(OutputDir, packageName + ".zip"),
            StagingRoot:       StagingRoot,
            DevId:             "dev",
            DevPassword:       "devpw",
            Username:          "user",
            Password:          "pw",
            MaxScrapesThisRun: maxScrapes,
            Force:             force,
            UpdatePayloads:    updatePayloads,
            IndexAfterBuild:   indexAfterBuild,
            KeepStaging:       keepStaging);

    private static FakeDetailsFetcher TwoGameFetcher() => new(new Dictionary<string, string?>
    {
        ["39874"] = """{"id":39874}""",
        ["39875"] = """{"id":39875}""",
    });

    private string StagingDir(string packageName = "test-package")
        => Path.Combine(StagingRoot, "screenscraper", packageName);

    private string PayloadsDir(string packageName = "test-package")
        => Path.Combine(StagingDir(packageName), "payloads");

    private long CatalogCount(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_catalog.DbPath}");
        conn.Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    // ── Existing tests (unchanged behavior) ───────────────────────────────────

    [Fact]
    public async Task Build_CreatesStagingFolder()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher());

        await builder.BuildAsync(opts);

        Assert.True(Directory.Exists(StagingDir()));
    }

    [Fact]
    public async Task Build_CopiesGamelistCsvToStaging()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher());

        await builder.BuildAsync(opts);

        Assert.True(File.Exists(Path.Combine(StagingDir(), "gameslist.csv")));
    }

    [Fact]
    public async Task Build_WritesPayloadJsonFilesToStaging()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher());

        await builder.BuildAsync(opts);

        Assert.True(File.Exists(Path.Combine(PayloadsDir(), "39874.json")));
        Assert.True(File.Exists(Path.Combine(PayloadsDir(), "39875.json")));
    }

    [Fact]
    public async Task Build_WritesProgressJson()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher());

        await builder.BuildAsync(opts);

        var progressPath = Path.Combine(StagingDir(), "progress.json");
        Assert.True(File.Exists(progressPath));
        var text = File.ReadAllText(progressPath);
        Assert.Contains("payloadsWritten", text);
        Assert.Contains("39875", text); // lastGameId
    }

    [Fact]
    public async Task Build_CompleteRun_CreatesOutputZip()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher());

        var result  = await builder.BuildAsync(opts);

        Assert.True(result.IsComplete);
        Assert.True(File.Exists(opts.OutputZipPath));
    }

    [Fact]
    public async Task Build_ZipContainsManifestCsvAndPayloads()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher());

        await builder.BuildAsync(opts);

        using var zip = ZipFile.OpenRead(opts.OutputZipPath);
        var entries   = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("manifest.json",       entries);
        Assert.Contains("gameslist.csv",       entries);
        Assert.Contains("payloads/39874.json", entries);
        Assert.Contains("payloads/39875.json", entries);
    }

    [Fact]
    public async Task Build_ReusesExistingStagedPayloadWhenForceIsFalse()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, force: false);
        var fetcher = TwoGameFetcher();

        Directory.CreateDirectory(PayloadsDir());
        File.WriteAllText(Path.Combine(PayloadsDir(), "39874.json"), """{"pre":"staged"}""");

        var builder = new ScreenScraperCachePackageBuilder(fetcher);
        var result  = await builder.BuildAsync(opts);

        Assert.Equal(1, result.AlreadyStaged);
        Assert.Equal(1, fetcher.FetchCount); // only 39875 fetched
    }

    [Fact]
    public async Task Build_RefetchesExistingStagedPayloadWhenForceIsTrue()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, force: true);
        var fetcher = TwoGameFetcher();

        Directory.CreateDirectory(PayloadsDir());
        File.WriteAllText(Path.Combine(PayloadsDir(), "39874.json"), """{"pre":"staged"}""");

        var builder = new ScreenScraperCachePackageBuilder(fetcher);
        await builder.BuildAsync(opts);

        Assert.Equal(2, fetcher.FetchCount);
    }

    [Fact]
    public async Task Build_ExistingZipWithForceIsFalse_ReturnsWasAlreadyBuilt()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, force: false);
        var fetcher = TwoGameFetcher();

        Directory.CreateDirectory(OutputDir);
        File.WriteAllBytes(opts.OutputZipPath, []);

        var builder = new ScreenScraperCachePackageBuilder(fetcher);
        var result  = await builder.BuildAsync(opts);

        Assert.True(result.WasAlreadyBuilt);
        Assert.Equal(0, fetcher.FetchCount);
    }

    [Fact]
    public async Task Build_RateLimit_LeavesStagingIntactAndDoesNotCreateZip()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(new RateLimitFetcher(throwAfterN: 0));

        var result  = await builder.BuildAsync(opts);

        Assert.True(result.HitRateLimit);
        Assert.False(result.IsComplete);
        Assert.False(File.Exists(opts.OutputZipPath));
        Assert.True(Directory.Exists(StagingDir()));
    }

    [Fact]
    public async Task Build_RateLimit_AfterSomeFetches_LeavesStagingIntactAndDoesNotCreateZip()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(new RateLimitFetcher(throwAfterN: 1));

        var result  = await builder.BuildAsync(opts);

        Assert.True(result.HitRateLimit);
        Assert.Equal(1, result.PayloadsWritten);
        Assert.False(File.Exists(opts.OutputZipPath));
        Assert.True(Directory.Exists(StagingDir()));
    }

    [Fact]
    public async Task Build_Cancellation_LeavesStagingIntactAndDoesNotCreateZip()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => builder.BuildAsync(opts, ct: cts.Token));

        Assert.False(File.Exists(opts.OutputZipPath));
        Assert.True(Directory.Exists(StagingDir()));
    }

    [Fact]
    public async Task Build_IndexAfterBuild_IndexesZipIntoDatabase()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, indexAfterBuild: true);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher(), catalog: _catalog);

        await builder.BuildAsync(opts);

        Assert.Equal(1L, CatalogCount("SELECT COUNT(*) FROM cache_packages"));
        Assert.Equal(2L, CatalogCount("SELECT COUNT(*) FROM cache_package_games"));
    }

    [Fact]
    public async Task Build_KeepStagingFalse_DeletesStagingAfterZip()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, keepStaging: false);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher());

        await builder.BuildAsync(opts);

        Assert.True(File.Exists(opts.OutputZipPath));
        Assert.False(Directory.Exists(StagingDir()));
    }

    [Fact] // updated: invalid rows do not block completion
    public async Task Build_InvalidRows_AreSkipped_AndDoNotBlockCompletion()
    {
        const string csvWithInvalid = """
            "Game ID";"Game Name"
            "39874";"1942"
            "";"Missing ID"
            "39875";"1943"
            """;
        var csv     = WriteCsv(csvWithInvalid);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher());

        var result  = await builder.BuildAsync(opts);

        Assert.Equal(2, result.ValidGames);
        Assert.Equal(1, result.SkippedRows);
        Assert.True(result.IsComplete);   // invalid rows don't count as missing payloads
        Assert.True(File.Exists(opts.OutputZipPath));
    }

    // ── Safe-limit / completeness tests ──────────────────────────────────────

    [Fact] // updated: safe limit must NOT create a partial ZIP
    public async Task Build_MaxScrapesThisRun_DoesNotCreatePartialZip()
    {
        const string csv5 = """
            "Game ID";"Game Name"
            "1";"Game One"
            "2";"Game Two"
            "3";"Game Three"
            "4";"Game Four"
            "5";"Game Five"
            """;
        var csv     = WriteCsv(csv5);
        var opts    = MakeOptions(csv, maxScrapes: 2);
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>());
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        var result  = await builder.BuildAsync(opts);

        Assert.Equal(2, fetcher.FetchCount);
        Assert.Equal(2, result.PayloadsWritten);
        Assert.True(result.HitSafeLimit);
        Assert.False(result.IsComplete);
        Assert.Equal(3, result.RemainingPayloads);
        Assert.False(File.Exists(opts.OutputZipPath)); // no partial ZIP
    }

    [Fact]
    public async Task Build_MaxScrapesThisRun_LeavesStagingIntact()
    {
        const string csv3 = """
            "Game ID";"Game Name"
            "1";"Game One"
            "2";"Game Two"
            "3";"Game Three"
            """;
        var csv     = WriteCsv(csv3);
        var opts    = MakeOptions(csv, maxScrapes: 1);
        var builder = new ScreenScraperCachePackageBuilder(new FakeDetailsFetcher(new Dictionary<string, string?>()));

        await builder.BuildAsync(opts);

        Assert.True(Directory.Exists(StagingDir()));
        Assert.True(File.Exists(Path.Combine(PayloadsDir(), "1.json")));  // first game was fetched
    }

    [Fact]
    public async Task Build_Resume_SecondRunCompletesAndCreatesZip()
    {
        // First run: maxScrapes=1 → fetches game 39874 only, safe limit hit, no ZIP
        var csv      = WriteCsv(ValidCsv);
        var opts     = MakeOptions(csv, maxScrapes: 1);
        var fetcher1 = TwoGameFetcher();
        var builder  = new ScreenScraperCachePackageBuilder(fetcher1);

        var run1 = await builder.BuildAsync(opts);
        Assert.False(run1.IsComplete);
        Assert.True(run1.HitSafeLimit);
        Assert.False(File.Exists(opts.OutputZipPath));

        // Second run: same options, maxScrapes=1 → 39874 already staged, fetches 39875 → complete
        var fetcher2 = TwoGameFetcher();
        var builder2 = new ScreenScraperCachePackageBuilder(fetcher2);

        var run2 = await builder2.BuildAsync(opts);
        Assert.True(run2.IsComplete);
        Assert.Equal(1, run2.AlreadyStaged);    // 39874 reused
        Assert.Equal(1, run2.PayloadsWritten);  // 39875 fetched
        Assert.Equal(1, fetcher2.FetchCount);
        Assert.True(File.Exists(opts.OutputZipPath));
    }

    [Fact]
    public async Task Build_ExistingStagedPayloadsCountTowardCompletion()
    {
        // Pre-stage both payloads — no fetches needed, should immediately be complete
        Directory.CreateDirectory(PayloadsDir());
        File.WriteAllText(Path.Combine(PayloadsDir(), "39874.json"), """{"id":39874}""");
        File.WriteAllText(Path.Combine(PayloadsDir(), "39875.json"), """{"id":39875}""");

        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, maxScrapes: 0); // zero budget — only staging can satisfy
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>());
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        var result  = await builder.BuildAsync(opts);

        Assert.Equal(0, fetcher.FetchCount);
        Assert.Equal(2, result.AlreadyStaged);
        Assert.True(result.IsComplete);
        Assert.True(File.Exists(opts.OutputZipPath));
    }

    [Fact]
    public async Task Build_FailedFetch_PreventsZipCreation()
    {
        // 39875 not found on ScreenScraper (null response) → no payload → not complete
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = """{"id":39874}""",
            ["39875"] = null,
        });
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        var result  = await builder.BuildAsync(opts);

        Assert.Equal(1, result.FailedFetches);
        Assert.Equal(1, result.PayloadsWritten);
        Assert.Equal(1, result.RemainingPayloads);
        Assert.False(result.IsComplete);
        Assert.False(File.Exists(opts.OutputZipPath));
    }

    [Fact] // spec test 3+4: stops BEFORE the next call, no retry
    public async Task Build_MaxScrapesThisRun_StopsBeforeNextApiCall_NoRetry()
    {
        // 3 games, maxScrapes=1:
        // - Game "1": scrapesThisRun(0) < 1 → fetched, scrapesThisRun=1
        // - Game "2": scrapesThisRun(1) >= 1 → break, NO call made
        // - Game "3": never reached
        const string csv3 = """
            "Game ID";"Game Name"
            "1";"Game One"
            "2";"Game Two"
            "3";"Game Three"
            """;
        var csv     = WriteCsv(csv3);
        var opts    = MakeOptions(csv, maxScrapes: 1);
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>());
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        var result  = await builder.BuildAsync(opts);

        Assert.Equal(1, fetcher.FetchCount);       // exactly maxScrapes calls — not maxScrapes+1
        Assert.True(result.HitSafeLimit);
        Assert.Equal(2, result.RemainingPayloads); // games 2 and 3 still missing
        Assert.False(result.IsComplete);
    }

    [Fact] // spec test 15: no .tmp survives an incomplete build
    public async Task Build_IncompleteRun_LeavesNoTmpFile()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, maxScrapes: 0); // immediate safe limit — no fetches
        var builder = new ScreenScraperCachePackageBuilder(
            new FakeDetailsFetcher(new Dictionary<string, string?>()));

        var result  = await builder.BuildAsync(opts);

        Assert.True(result.HitSafeLimit);
        Assert.False(result.IsComplete);
        Assert.False(File.Exists(opts.OutputZipPath));
        Assert.False(File.Exists(opts.OutputZipPath + ".tmp"));
    }

    // ── Media download tests ──────────────────────────────────────────────────

    private string MediaDir(string packageName = "test-package")
        => Path.Combine(StagingDir(packageName), "media");

    [Fact]
    public async Task Build_WithMedia_WritesMediaFilesToStagingDir()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), new FakeMediaDownloader());

        await builder.BuildAsync(opts);

        Assert.True(Directory.Exists(Path.Combine(MediaDir(), "screenshot")));
        Assert.True(Directory.Exists(Path.Combine(MediaDir(), "cover-front")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(MediaDir(), "screenshot")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(MediaDir(), "cover-front")));
    }

    [Fact]
    public async Task Build_WithMedia_ZipContainsMediaEntries()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), new FakeMediaDownloader());

        await builder.BuildAsync(opts);

        using var zip     = ZipFile.OpenRead(opts.OutputZipPath);
        var       entries = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(entries, e => e.StartsWith("media/screenshot/"));
        Assert.Contains(entries, e => e.StartsWith("media/cover-front/"));
    }

    [Fact]
    public async Task Build_WithMedia_ReturnsCorrectMediaWrittenCount()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), new FakeMediaDownloader());

        var result = await builder.BuildAsync(opts);

        Assert.Equal(4, result.MediaWritten);        // 2 games × 2 media items each
        Assert.Equal(0, result.AlreadyStagedMedia);
        Assert.Equal(0, result.FailedMediaDownloads);
    }

    [Fact]
    public async Task Build_WithMedia_StagedPayload_ExistingMediaCounted()
    {
        // Pre-stage: payload for 39874 and 1 media file for it
        Directory.CreateDirectory(PayloadsDir());
        File.WriteAllText(Path.Combine(PayloadsDir(), "39874.json"), "{}");
        var ssDir = Path.Combine(MediaDir(), "screenshot");
        Directory.CreateDirectory(ssDir);
        File.WriteAllBytes(Path.Combine(ssDir, "39874_0.png"), [1, 2, 3]);

        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, force: false);
        var builder = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), new FakeMediaDownloader());

        var result = await builder.BuildAsync(opts);

        // 39874: staged → alreadyStaged=1, alreadyStagedMedia=1 (from dir scan)
        // 39875: fetched → 2 media items written
        Assert.Equal(1, result.AlreadyStaged);
        Assert.Equal(1, result.AlreadyStagedMedia);
        Assert.Equal(2, result.MediaWritten);
    }

    [Fact]
    public async Task Build_WithMedia_ExistingMediaForFreshGame_AlreadyStagedMedia()
    {
        // Pre-stage a media file but NOT the payload for 39874
        var ssDir = Path.Combine(MediaDir(), "screenshot");
        Directory.CreateDirectory(ssDir);
        File.WriteAllBytes(Path.Combine(ssDir, "39874_0.png"), [1, 2, 3]);

        var csv        = WriteCsv(ValidCsv);
        var opts       = MakeOptions(csv, force: false);
        var downloader = new FakeMediaDownloader();
        var builder    = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), downloader);

        var result = await builder.BuildAsync(opts);

        // 39874: screenshot already staged (pre-check in DownloadGameMediaAsync) + cover-front downloaded
        // 39875: 2 items downloaded
        Assert.Equal(1, result.AlreadyStagedMedia);
        Assert.Equal(3, result.MediaWritten);
        Assert.Equal(3, downloader.DownloadCount);
    }

    [Fact]
    public async Task Build_WithMedia_ForceTrue_RedownloadsExistingMedia()
    {
        // Pre-stage a media file for 39874 (would normally be counted as already-staged)
        var ssDir = Path.Combine(MediaDir(), "screenshot");
        Directory.CreateDirectory(ssDir);
        File.WriteAllBytes(Path.Combine(ssDir, "39874_0.png"), [1, 2, 3]);

        var csv        = WriteCsv(ValidCsv);
        var opts       = MakeOptions(csv, force: true);
        var downloader = new FakeMediaDownloader();
        var builder    = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), downloader);

        var result = await builder.BuildAsync(opts);

        // force=true: pre-existing file deleted, all 4 items re-downloaded
        Assert.Equal(0, result.AlreadyStagedMedia);
        Assert.Equal(4, result.MediaWritten);
        Assert.Equal(4, downloader.DownloadCount);
    }

    [Fact]
    public async Task Build_WithMedia_MediaRateLimit_AbortsEarlyAndNoZip()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(
            new MediaFakeDetailsFetcher(),
            new FakeMediaDownloader(rateLimitAfterN: 0));  // rate-limit on first media download

        var result = await builder.BuildAsync(opts);

        Assert.True(result.HitRateLimit);
        Assert.False(result.IsComplete);
        Assert.False(File.Exists(opts.OutputZipPath));
        Assert.Equal(1, result.PayloadsWritten);  // 39874 payload was fetched before rate-limit
    }

    [Fact]
    public async Task Build_NoMediaDownloader_BuildsNormally()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(TwoGameFetcher()); // no media downloader

        var result = await builder.BuildAsync(opts);

        Assert.True(result.IsComplete);
        Assert.Equal(0, result.MediaWritten);
        Assert.Equal(0, result.AlreadyStagedMedia);
        Assert.Equal(0, result.FailedMediaDownloads);
        Assert.True(File.Exists(opts.OutputZipPath));
    }

    [Fact]
    public async Task Build_WithMedia_FailedDownload_DoesNotPreventZip()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(
            new MediaFakeDetailsFetcher(),
            new FakeMediaDownloader(throwAfterN: 0)); // all downloads throw IOException

        var result = await builder.BuildAsync(opts);

        Assert.True(result.IsComplete);           // payloads complete → still creates ZIP
        Assert.Equal(4, result.FailedMediaDownloads);
        Assert.Equal(0, result.MediaWritten);
        Assert.True(File.Exists(opts.OutputZipPath));
    }

    [Fact]
    public async Task Build_WithMedia_ManifestHasPayloadCountAndMediaCount()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), new FakeMediaDownloader());

        await builder.BuildAsync(opts);

        using var zip    = ZipFile.OpenRead(opts.OutputZipPath);
        using var stream = (zip.GetEntry("manifest.json") ?? throw new Exception("no manifest")).Open();
        using var doc    = System.Text.Json.JsonDocument.Parse(stream);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("payloadCount").GetInt32());
        Assert.Equal(4, root.GetProperty("mediaCount").GetInt32());   // 2 games × 2 items
    }

    [Fact]
    public async Task Build_WithMedia_SecondRun_AllStagedNoNewDownloads()
    {
        var csv  = WriteCsv(ValidCsv);
        var opts = MakeOptions(csv);

        // Run 1: complete build — produces payloads + media in staging
        await new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), new FakeMediaDownloader())
            .BuildAsync(opts);

        // Delete the ZIP so the already-built guard doesn't fire
        File.Delete(opts.OutputZipPath);

        var downloader2 = new FakeMediaDownloader();
        var result2 = await new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), downloader2)
            .BuildAsync(opts);

        Assert.Equal(2, result2.AlreadyStaged);
        Assert.Equal(4, result2.AlreadyStagedMedia);  // 2 files per game found by dir scan
        Assert.Equal(0, result2.MediaWritten);
        Assert.Equal(0, downloader2.DownloadCount);
        Assert.True(result2.IsComplete);
    }

    [Fact]
    public async Task Build_WithMedia_ManifestHasMediaCountByType()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), new FakeMediaDownloader());

        await builder.BuildAsync(opts);

        using var zip    = ZipFile.OpenRead(opts.OutputZipPath);
        using var stream = (zip.GetEntry("manifest.json") ?? throw new Exception("no manifest")).Open();
        using var doc    = System.Text.Json.JsonDocument.Parse(stream);
        var byType = doc.RootElement.GetProperty("mediaCountByType");

        Assert.Equal(2, byType.GetProperty("screenshot").GetInt32());    // 39874 + 39875
        Assert.Equal(2, byType.GetProperty("cover-front").GetInt32());   // 39874 + 39875
    }

    [Fact]
    public async Task Build_WithMedia_ManifestHasMediaTypesSorted()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), new FakeMediaDownloader());

        await builder.BuildAsync(opts);

        using var zip    = ZipFile.OpenRead(opts.OutputZipPath);
        using var stream = (zip.GetEntry("manifest.json") ?? throw new Exception("no manifest")).Open();
        using var doc    = System.Text.Json.JsonDocument.Parse(stream);
        var mediaTypes = doc.RootElement.GetProperty("mediaTypes")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        Assert.Equal(new[] { "cover-front", "screenshot" }, mediaTypes);
    }

    [Fact]
    public async Task Build_WithMedia_StagingCreatesMediaSubdirs()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), new FakeMediaDownloader());

        await builder.BuildAsync(opts);

        Assert.True(Directory.Exists(MediaDir()));
        Assert.True(Directory.Exists(Path.Combine(MediaDir(), "screenshot")));
        Assert.True(Directory.Exists(Path.Combine(MediaDir(), "cover-front")));
    }

    // ── Failed fetch diagnostics ──────────────────────────────────────────────

    [Fact]
    public async Task Build_FailedFetch_IncrementsFailedFetches()
    {
        // FakeDetailsFetcher returns null for game "39874" → failedFetches should be 1
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = null,
            ["39875"] = """{"id":39875}""",
        });
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        var result = await builder.BuildAsync(opts);

        Assert.Equal(1, result.FailedFetches);
    }

    [Fact]
    public async Task Build_AllFailed_IsNotComplete_NoZip()
    {
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = null,
            ["39875"] = null,
        });
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        var result = await builder.BuildAsync(opts);

        Assert.Equal(2, result.FailedFetches);
        Assert.False(result.IsComplete);
        Assert.False(File.Exists(opts.OutputZipPath));
    }

    [Fact]
    public async Task Build_FailedFetch_ProgressReports_FailMessage()
    {
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = null,
            ["39875"] = """{"id":39875}""",
        });
        var csv      = WriteCsv(ValidCsv);
        var opts     = MakeOptions(csv);
        var builder  = new ScreenScraperCachePackageBuilder(fetcher);
        var messages = new System.Collections.Generic.List<string>();
        var progress = new Progress<string>(m => messages.Add(m));

        await builder.BuildAsync(opts, progress);

        Assert.Contains(messages, m => m.Contains("FAIL") && m.Contains("39874"));
    }

    [Fact]
    public async Task Build_FailedFetch_RemainingPayloads_ReflectsMissing()
    {
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = null,
            ["39875"] = """{"id":39875}""",
        });
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        var result = await builder.BuildAsync(opts);

        Assert.Equal(2, result.ValidGames);
        Assert.Equal(1, result.PayloadsAvailable);
        Assert.Equal(1, result.RemainingPayloads);
    }

    // ── UpdatePayloads tests ──────────────────────────────────────────────────

    [Fact]
    public async Task Build_UpdatePayloadsFalse_ExistingPayload_NotRefetched()
    {
        // Pre-stage both payloads; UpdatePayloads=false → fetcher must NOT be called
        Directory.CreateDirectory(PayloadsDir());
        File.WriteAllText(Path.Combine(PayloadsDir(), "39874.json"), """{"pre":"staged"}""");
        File.WriteAllText(Path.Combine(PayloadsDir(), "39875.json"), """{"pre":"staged2"}""");

        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, updatePayloads: false);
        var fetcher = TwoGameFetcher();
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        var result = await builder.BuildAsync(opts);

        Assert.Equal(0, fetcher.FetchCount);
        Assert.Equal(2, result.AlreadyStaged);
        Assert.Equal(0, result.PayloadsWritten);
    }

    [Fact]
    public async Task Build_UpdatePayloadsTrue_UnchangedPayload_ReusesMediaAndCountsAsAlreadyStaged()
    {
        // Pre-stage payload matching what the fetcher will return; media file already present
        Directory.CreateDirectory(PayloadsDir());
        File.WriteAllText(Path.Combine(PayloadsDir(), "39874.json"), """{"id":39874}""");

        var ssDir = Path.Combine(MediaDir(), "screenshot");
        Directory.CreateDirectory(ssDir);
        File.WriteAllBytes(Path.Combine(ssDir, "39874_0.png"), [1, 2, 3]);

        var csv        = WriteCsv(ValidCsv);
        var opts       = MakeOptions(csv, updatePayloads: true);
        var downloader = new FakeMediaDownloader();
        var builder    = new ScreenScraperCachePackageBuilder(
            new FakeDetailsFetcher(new Dictionary<string, string?> { ["39874"] = """{"id":39874}""" }),
            downloader);

        var result = await builder.BuildAsync(opts);

        // 39874: fetched, JSON identical → alreadyStaged; existing screenshot counted via dir scan
        // 39875: new fetch → written + media downloaded
        Assert.Equal(1, result.AlreadyStaged);    // 39874 unchanged
        Assert.Equal(1, result.PayloadsWritten);  // 39875 new
        Assert.Equal(0, result.FailedFetches);
        // 39874 media not re-downloaded
        // 39874 media reused via dir-scan (not re-downloaded)
        Assert.True(result.AlreadyStagedMedia >= 1);
    }

    [Fact]
    public async Task Build_UpdatePayloadsTrue_ChangedPayload_OverwritesAndDownloadsMissingMedia()
    {
        // Pre-stage payload with OLD content; fetcher returns NEW content → payload overwritten
        Directory.CreateDirectory(PayloadsDir());
        File.WriteAllText(Path.Combine(PayloadsDir(), "39874.json"), """{"old":true}""");

        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, updatePayloads: true);
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = """{"id":39874,"updated":true}""",
        });
        var downloader = new FakeMediaDownloader();
        var builder    = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), downloader);

        // Use a fetcher that returns different content for 39874 only
        var fetcherWithDiff = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = """{"id":39874,"updated":true}""",
            ["39875"] = """{"id":39875}""",
        });
        var builder2 = new ScreenScraperCachePackageBuilder(fetcherWithDiff, downloader);

        var result = await builder2.BuildAsync(opts);

        // 39874: payload changed → written; media downloaded (none pre-staged)
        Assert.Equal(2, result.PayloadsWritten);  // both fetched (39874 changed, 39875 new)
        Assert.Equal(0, result.AlreadyStaged);
        var newJson = File.ReadAllText(Path.Combine(PayloadsDir(), "39874.json"), Encoding.UTF8);
        Assert.Equal("""{"id":39874,"updated":true}""", newJson);
    }

    [Fact]
    public async Task Build_UpdatePayloadsTrue_ChangedPayload_DoesNotRedownloadExistingMedia()
    {
        // Single game; pre-stage old payload and one of its two media files
        const string csv1 = """
            "Game ID";"Game Name"
            "39874";"1942"
            """;

        // Pre-stage old payload — differs from what MediaFakeDetailsFetcher returns: {"id":39874}
        Directory.CreateDirectory(PayloadsDir());
        File.WriteAllText(Path.Combine(PayloadsDir(), "39874.json"), """{"old":true}""");

        // Pre-stage the screenshot (one of the two media items MediaFakeDetailsFetcher yields)
        var ssDir = Path.Combine(MediaDir(), "screenshot");
        Directory.CreateDirectory(ssDir);
        File.WriteAllBytes(Path.Combine(ssDir, "39874_0.png"), [1, 2, 3]);

        var opts       = MakeOptions(WriteCsv(csv1), updatePayloads: true);
        var downloader = new FakeMediaDownloader();
        var builder    = new ScreenScraperCachePackageBuilder(new MediaFakeDetailsFetcher(), downloader);

        var result = await builder.BuildAsync(opts);

        // Payload changed ({"old":true} ≠ {"id":39874}) → overwritten; media checked with force=false:
        //   screenshot already staged → alreadyStagedMedia=1; cover-front missing → downloaded
        Assert.Equal(1, result.PayloadsWritten);
        Assert.Equal(0, result.AlreadyStaged);
        Assert.Equal(1, result.AlreadyStagedMedia);
        Assert.Equal(1, result.MediaWritten);
        Assert.Equal(1, downloader.DownloadCount);
    }

    // ── Payload sanitization tests ────────────────────────────────────────────

    private const string CredentialJson =
        """{"url":"https://ss.fr/api?devid=DEVID123&devpassword=DEVPW&ssid=USER&sspassword=USERPW&softname=MyApp&gameId=39874"}""";

    [Fact]
    public async Task Build_StagedPayload_DoesNotContainCredentials()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = CredentialJson,
            ["39875"] = CredentialJson,
        });
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        await builder.BuildAsync(opts);

        foreach (var file in Directory.EnumerateFiles(PayloadsDir(), "*.json"))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            Assert.DoesNotContain("DEVID123",  text);
            Assert.DoesNotContain("DEVPW",     text);
            Assert.DoesNotContain("USER",      text);
            Assert.DoesNotContain("USERPW",    text);
            Assert.DoesNotContain("MyApp",     text);
            Assert.Contains("<DEVID>",         text);
            Assert.Contains("<SSID>",          text);
        }
    }

    [Fact]
    public async Task Build_ZipPayload_DoesNotContainCredentials()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = CredentialJson,
            ["39875"] = CredentialJson,
        });
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        await builder.BuildAsync(opts);

        using var zip = ZipFile.OpenRead(opts.OutputZipPath);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("payloads/")))
        {
            using var stream = entry.Open();
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            Assert.DoesNotContain("DEVID123", text);
            Assert.DoesNotContain("USERPW",   text);
            Assert.Contains("<DEVID>",        text);
        }
    }

    [Fact]
    public async Task Build_UpdatePayloads_CredentialOnlyDiff_TreatedAsUnchanged()
    {
        // Pre-stage already-sanitized payload (what a previous run would have written)
        const string sanitized =
            """{"url":"?devid=<DEVID>&ssid=<SSID>&gameId=39874"}""";
        Directory.CreateDirectory(PayloadsDir());
        File.WriteAllText(Path.Combine(PayloadsDir(), "39874.json"), sanitized, Encoding.UTF8);

        // Fetcher returns same content but with real credentials (unsanitized)
        const string unsanitized =
            """{"url":"?devid=REALDEV&ssid=REALUSER&gameId=39874"}""";

        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, updatePayloads: true);
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = unsanitized,
            ["39875"] = """{"id":39875}""",
        });
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        var result = await builder.BuildAsync(opts);

        // 39874: sanitized new == existing staged → treated as unchanged
        Assert.Equal(1, result.AlreadyStaged);
        Assert.Equal(1, result.PayloadsWritten);  // only 39875 is new
    }

    // ── response.ssuser removal ───────────────────────────────────────────────

    private const string SsuserJson =
        """{"response":{"ssuser":{"id":"Scanno","numid":"26962815","maxthreads":"1"},"jeu":{"id":"39874","noms":[{"region":"wor","text":"1942"}]}}}""";

    [Fact]
    public async Task Build_StagedPayload_DoesNotContainSsuser()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = SsuserJson,
            ["39875"] = SsuserJson,
        });
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        await builder.BuildAsync(opts);

        foreach (var file in Directory.EnumerateFiles(PayloadsDir(), "*.json"))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            Assert.DoesNotContain("ssuser",   text);
            Assert.DoesNotContain("Scanno",   text);
            Assert.DoesNotContain("26962815", text);
        }
    }

    [Fact]
    public async Task Build_ZipPayload_DoesNotContainSsuser()
    {
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv);
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = SsuserJson,
            ["39875"] = SsuserJson,
        });
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        await builder.BuildAsync(opts);

        using var zip = ZipFile.OpenRead(opts.OutputZipPath);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("payloads/")))
        {
            using var stream = entry.Open();
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            Assert.DoesNotContain("ssuser",   text);
            Assert.DoesNotContain("Scanno",   text);
            Assert.DoesNotContain("26962815", text);
        }
    }

    [Fact]
    public async Task Build_UpdatePayloads_SsuserOnlyDiff_TreatedAsUnchanged()
    {
        // Pre-stage a sanitized payload (ssuser already stripped by previous run)
        const string withSsuser    = SsuserJson;
        var          sanitizedOnce = ScreenScraperPayloadSanitizer.SanitizeJson(withSsuser);
        Directory.CreateDirectory(PayloadsDir());
        File.WriteAllText(Path.Combine(PayloadsDir(), "39874.json"), sanitizedOnce, Encoding.UTF8);

        // Fetcher returns same game data but with ssuser (raw API response)
        var csv     = WriteCsv(ValidCsv);
        var opts    = MakeOptions(csv, updatePayloads: true);
        var fetcher = new FakeDetailsFetcher(new Dictionary<string, string?>
        {
            ["39874"] = withSsuser,
            ["39875"] = """{"response":{"jeu":{"id":"39875"}}}""",
        });
        var builder = new ScreenScraperCachePackageBuilder(fetcher);

        var result = await builder.BuildAsync(opts);

        // 39874: ssuser removed from both sides → treated as unchanged
        Assert.Equal(1, result.AlreadyStaged);
        Assert.Equal(1, result.PayloadsWritten);  // only 39875 is new
    }
}
