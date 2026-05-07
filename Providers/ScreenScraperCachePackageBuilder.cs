using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Data;

namespace Arkadia.Providers;

// ── Option / result records ───────────────────────────────────────────────────

public sealed record ScreenScraperCachePackageBuildOptions(
    string CsvPath,
    string SystemId,
    string SystemName,
    string PackageName,
    string OutputZipPath,
    string StagingRoot,
    string DevId,
    string DevPassword,
    string Username,
    string Password,
    int  MaxScrapesThisRun = 19000,
    bool Force             = false,
    bool UpdatePayloads    = false,
    bool IndexAfterBuild   = true,
    bool KeepStaging       = true);

public sealed record ScreenScraperCachePackageBuildResult(
    string OutputZipPath,
    string StagingPath,
    int    ValidGames,           // valid game rows parsed from CSV
    int    PayloadsWritten,      // new payloads fetched and written this run
    int    AlreadyStaged,        // payloads reused from a previous run
    int    PayloadsAvailable,    // PayloadsWritten + AlreadyStaged
    int    RemainingPayloads,    // ValidGames - PayloadsAvailable
    int    SkippedRows,          // invalid CSV rows skipped (empty id/name, parse errors)
    int    FailedFetches,        // API returned null (game not found on ScreenScraper)
    int    MediaWritten,         // new media files downloaded this run
    int    AlreadyStagedMedia,   // media files reused from a previous run
    int    FailedMediaDownloads, // media downloads that returned null or threw non-rate-limit errors
    bool   HitRateLimit,         // stopped because ScreenScraper returned 429
    bool   HitSafeLimit,         // stopped because MaxScrapesThisRun was reached
    bool   IsComplete,           // all valid games have staged payloads → ZIP was created
    bool   WasAlreadyBuilt);     // output ZIP already existed and Force=false

// ── Builder ───────────────────────────────────────────────────────────────────

/// <summary>
/// Builds a ScreenScraper cache ZIP package.
///
/// Staging layout:
///   &lt;StagingRoot&gt;/screenscraper/&lt;PackageName&gt;/
///     gameslist.csv
///     progress.json
///     manifest.partial.json
///     payloads/
///       &lt;gameId&gt;.json
///     media/
///       &lt;type&gt;/
///         &lt;gameId&gt;[_&lt;region&gt;]_&lt;index&gt;.&lt;ext&gt;
///
/// Final ZIP (created only when IsComplete):
///   &lt;OutputZipPath&gt;
///     manifest.json
///     gameslist.csv
///     payloads/*.json
///     media/&lt;type&gt;/&lt;gameId&gt;[_&lt;region&gt;]_&lt;index&gt;.&lt;ext&gt;
/// </summary>
public sealed class ScreenScraperCachePackageBuilder(
    IScreenScraperDetailsFetcher fetcher,
    IMediaDownloader?            mediaDownloader = null,
    CatalogService?              catalog         = null)
{
    private static readonly Regex CsvLineRx = new(
        @"^""(?<id>[^""]*)"";""(?<name>.*)""$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    // ── Per-game media item ───────────────────────────────────────────────────

    private readonly record struct MediaEntry(
        string                Type,
        string                Region,
        IReadOnlyList<string> ValidExts,
        string                Url,
        string                Format,
        long?                 Size,
        int                   Index);

    // ── Per-game media download summary ──────────────────────────────────────

    private sealed record MediaDownloadResult(
        int  Written,
        int  AlreadyStaged,
        int  Failed,
        bool HitRateLimit);

    // ── BuildAsync ────────────────────────────────────────────────────────────

    public async Task<ScreenScraperCachePackageBuildResult> BuildAsync(
        ScreenScraperCachePackageBuildOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var stagingDir = Path.Combine(options.StagingRoot, "screenscraper", options.PackageName);

        // ── Already-built guard ───────────────────────────────────────────────
        if (File.Exists(options.OutputZipPath) && !options.Force)
            return new ScreenScraperCachePackageBuildResult(
                OutputZipPath:       options.OutputZipPath,
                StagingPath:         stagingDir,
                ValidGames:          0,
                PayloadsWritten:     0,
                AlreadyStaged:       0,
                PayloadsAvailable:   0,
                RemainingPayloads:   0,
                SkippedRows:         0,
                FailedFetches:       0,
                MediaWritten:        0,
                AlreadyStagedMedia:  0,
                FailedMediaDownloads: 0,
                HitRateLimit:        false,
                HitSafeLimit:        false,
                IsComplete:          true,
                WasAlreadyBuilt:     true);

        // ── Staging setup ─────────────────────────────────────────────────────
        var payloadsDir = Path.Combine(stagingDir, "payloads");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(payloadsDir);

        var stagingCsvPath = Path.Combine(stagingDir, "gameslist.csv");
        File.Copy(options.CsvPath, stagingCsvPath, overwrite: true);

        // ── Parse CSV ─────────────────────────────────────────────────────────
        var (games, skippedRows) = ParseCsv(options.CsvPath);

        // ── Fetch loop ────────────────────────────────────────────────────────
        int  payloadsWritten      = 0;
        int  alreadyStaged        = 0;
        int  failedFetches        = 0;
        int  scrapesThisRun       = 0;
        int  mediaWritten         = 0;
        int  alreadyStagedMedia   = 0;
        int  failedMediaDownloads = 0;
        bool hitRateLimit         = false;
        bool hitSafeLimit         = false;
        string lastGameId         = "";

        foreach (var (gameId, title) in games)
        {
            ct.ThrowIfCancellationRequested();

            var payloadPath = Path.Combine(payloadsDir, $"{gameId}.json");

            if (File.Exists(payloadPath) && !options.Force)
            {
                if (!options.UpdatePayloads)
                {
                    alreadyStaged++;
                    if (mediaDownloader is not null)
                        alreadyStagedMedia += CountStagedMediaForGame(stagingDir, gameId);
                }
                else
                {
                    // Re-fetch and compare; overwrite only if payload changed
                    if (scrapesThisRun >= options.MaxScrapesThisRun)
                    {
                        hitSafeLimit = true;
                        break;
                    }

                    ScreenScraperResult? refreshed;
                    try
                    {
                        refreshed = await fetcher.FetchAsync(gameId, ct);
                    }
                    catch (ScreenScraperRateLimitException)
                    {
                        hitRateLimit = true;
                        WriteProgress(stagingDir, games.Count, payloadsWritten, alreadyStaged, skippedRows,
                                      failedFetches, mediaWritten, alreadyStagedMedia, failedMediaDownloads,
                                      hitRateLimit, hitSafeLimit, lastGameId);
                        int avail0 = payloadsWritten + alreadyStaged;
                        return new ScreenScraperCachePackageBuildResult(
                            OutputZipPath:        "",
                            StagingPath:          stagingDir,
                            ValidGames:           games.Count,
                            PayloadsWritten:      payloadsWritten,
                            AlreadyStaged:        alreadyStaged,
                            PayloadsAvailable:    avail0,
                            RemainingPayloads:    games.Count - avail0,
                            SkippedRows:          skippedRows,
                            FailedFetches:        failedFetches,
                            MediaWritten:         mediaWritten,
                            AlreadyStagedMedia:   alreadyStagedMedia,
                            FailedMediaDownloads: failedMediaDownloads,
                            HitRateLimit:         true,
                            HitSafeLimit:         false,
                            IsComplete:           false,
                            WasAlreadyBuilt:      false);
                    }

                    scrapesThisRun++;

                    if (refreshed is null)
                    {
                        // Refresh failed: preserve old payload, treat as still staged
                        failedFetches++;
                        progress?.Report($"[FAIL {gameId}] {title} — refresh failed, keeping existing payload (failed: {failedFetches})");
                        alreadyStaged++;
                        if (mediaDownloader is not null)
                            alreadyStagedMedia += CountStagedMediaForGame(stagingDir, gameId);
                    }
                    else
                    {
                        var existingJson  = File.ReadAllText(payloadPath, Encoding.UTF8);
                        var sanitizedNew = ScreenScraperPayloadSanitizer.SanitizeJson(refreshed.RawJson);
                        if (string.Equals(existingJson, sanitizedNew, StringComparison.Ordinal))
                        {
                            // Payload unchanged: reuse all existing media
                            alreadyStaged++;
                            if (mediaDownloader is not null)
                                alreadyStagedMedia += CountStagedMediaForGame(stagingDir, gameId);
                        }
                        else
                        {
                            // Payload changed: overwrite and download only missing/new media
                            File.WriteAllText(payloadPath, sanitizedNew, Encoding.UTF8);
                            payloadsWritten++;

                            if (mediaDownloader is not null)
                            {
                                var dlUpd = await DownloadGameMediaAsync(
                                    refreshed, gameId, stagingDir, force: false, mediaDownloader, ct);
                                mediaWritten         += dlUpd.Written;
                                alreadyStagedMedia   += dlUpd.AlreadyStaged;
                                failedMediaDownloads += dlUpd.Failed;
                                if (dlUpd.HitRateLimit)
                                {
                                    hitRateLimit = true;
                                    WriteProgress(stagingDir, games.Count, payloadsWritten, alreadyStaged, skippedRows,
                                                  failedFetches, mediaWritten, alreadyStagedMedia, failedMediaDownloads,
                                                  hitRateLimit, hitSafeLimit, lastGameId);
                                    int avail1 = payloadsWritten + alreadyStaged;
                                    return new ScreenScraperCachePackageBuildResult(
                                        OutputZipPath:        "",
                                        StagingPath:          stagingDir,
                                        ValidGames:           games.Count,
                                        PayloadsWritten:      payloadsWritten,
                                        AlreadyStaged:        alreadyStaged,
                                        PayloadsAvailable:    avail1,
                                        RemainingPayloads:    games.Count - avail1,
                                        SkippedRows:          skippedRows,
                                        FailedFetches:        failedFetches,
                                        MediaWritten:         mediaWritten,
                                        AlreadyStagedMedia:   alreadyStagedMedia,
                                        FailedMediaDownloads: failedMediaDownloads,
                                        HitRateLimit:         true,
                                        HitSafeLimit:         false,
                                        IsComplete:           false,
                                        WasAlreadyBuilt:      false);
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                if (scrapesThisRun >= options.MaxScrapesThisRun)
                {
                    hitSafeLimit = true;
                    break;
                }

                ScreenScraperResult? result;
                try
                {
                    result = await fetcher.FetchAsync(gameId, ct);
                }
                catch (ScreenScraperRateLimitException)
                {
                    hitRateLimit = true;
                    WriteProgress(stagingDir, games.Count, payloadsWritten, alreadyStaged, skippedRows,
                                  failedFetches, mediaWritten, alreadyStagedMedia, failedMediaDownloads,
                                  hitRateLimit, hitSafeLimit, lastGameId);
                    int avail = payloadsWritten + alreadyStaged;
                    return new ScreenScraperCachePackageBuildResult(
                        OutputZipPath:        "",
                        StagingPath:          stagingDir,
                        ValidGames:           games.Count,
                        PayloadsWritten:      payloadsWritten,
                        AlreadyStaged:        alreadyStaged,
                        PayloadsAvailable:    avail,
                        RemainingPayloads:    games.Count - avail,
                        SkippedRows:          skippedRows,
                        FailedFetches:        failedFetches,
                        MediaWritten:         mediaWritten,
                        AlreadyStagedMedia:   alreadyStagedMedia,
                        FailedMediaDownloads: failedMediaDownloads,
                        HitRateLimit:         true,
                        HitSafeLimit:         false,
                        IsComplete:           false,
                        WasAlreadyBuilt:      false);
                }

                if (result is null)
                {
                    failedFetches++;
                    progress?.Report($"[FAIL {gameId}] {title} — not found on ScreenScraper (failed: {failedFetches})");
                }
                else
                {
                    File.WriteAllText(payloadPath, ScreenScraperPayloadSanitizer.SanitizeJson(result.RawJson), Encoding.UTF8);
                    payloadsWritten++;
                }

                scrapesThisRun++;

                if (result is not null && mediaDownloader is not null)
                {
                    var dlResult = await DownloadGameMediaAsync(
                        result, gameId, stagingDir, options.Force, mediaDownloader, ct);
                    mediaWritten         += dlResult.Written;
                    alreadyStagedMedia   += dlResult.AlreadyStaged;
                    failedMediaDownloads += dlResult.Failed;
                    if (dlResult.HitRateLimit)
                    {
                        hitRateLimit = true;
                        WriteProgress(stagingDir, games.Count, payloadsWritten, alreadyStaged, skippedRows,
                                      failedFetches, mediaWritten, alreadyStagedMedia, failedMediaDownloads,
                                      hitRateLimit, hitSafeLimit, lastGameId);
                        int avail = payloadsWritten + alreadyStaged;
                        return new ScreenScraperCachePackageBuildResult(
                            OutputZipPath:        "",
                            StagingPath:          stagingDir,
                            ValidGames:           games.Count,
                            PayloadsWritten:      payloadsWritten,
                            AlreadyStaged:        alreadyStaged,
                            PayloadsAvailable:    avail,
                            RemainingPayloads:    games.Count - avail,
                            SkippedRows:          skippedRows,
                            FailedFetches:        failedFetches,
                            MediaWritten:         mediaWritten,
                            AlreadyStagedMedia:   alreadyStagedMedia,
                            FailedMediaDownloads: failedMediaDownloads,
                            HitRateLimit:         true,
                            HitSafeLimit:         false,
                            IsComplete:           false,
                            WasAlreadyBuilt:      false);
                    }
                }
            }

            lastGameId = gameId;
            WriteProgress(stagingDir, games.Count, payloadsWritten, alreadyStaged, skippedRows,
                          failedFetches, mediaWritten, alreadyStagedMedia, failedMediaDownloads,
                          hitRateLimit, hitSafeLimit, lastGameId);
            progress?.Report($"[{gameId}] {title}");
        }

        // ── Completeness check ────────────────────────────────────────────────
        int  payloadsAvailable = payloadsWritten + alreadyStaged;
        bool isComplete        = !hitSafeLimit && !hitRateLimit && payloadsAvailable == games.Count;

        // Collect media counts from staging dir for manifest
        var mediaByType      = CollectMediaByType(Path.Combine(stagingDir, "media"));
        int totalMediaOnDisk = mediaByType.Values.Sum();

        var manifestJson = BuildManifest(options, payloadsAvailable, totalMediaOnDisk, mediaByType);
        File.WriteAllText(Path.Combine(stagingDir, "manifest.partial.json"), manifestJson, Encoding.UTF8);

        // ── Create ZIP only when complete ─────────────────────────────────────
        if (isComplete)
        {
            var outputDir = Path.GetDirectoryName(options.OutputZipPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            var tmpZip = options.OutputZipPath + ".tmp";
            try
            {
                CreateZip(tmpZip, stagingDir, payloadsDir, manifestJson);
                File.Move(tmpZip, options.OutputZipPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmpZip); } catch { }
                throw;
            }

            if (options.IndexAfterBuild && catalog is not null)
                new ScreenScraperCachePackageImporter(catalog).IndexPackage(options.OutputZipPath);

            if (!options.KeepStaging)
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }

        return new ScreenScraperCachePackageBuildResult(
            OutputZipPath:       isComplete ? options.OutputZipPath : "",
            StagingPath:         stagingDir,
            ValidGames:          games.Count,
            PayloadsWritten:     payloadsWritten,
            AlreadyStaged:       alreadyStaged,
            PayloadsAvailable:   payloadsAvailable,
            RemainingPayloads:   games.Count - payloadsAvailable,
            SkippedRows:         skippedRows,
            FailedFetches:       failedFetches,
            MediaWritten:        mediaWritten,
            AlreadyStagedMedia:  alreadyStagedMedia,
            FailedMediaDownloads: failedMediaDownloads,
            HitRateLimit:        hitRateLimit,
            HitSafeLimit:        hitSafeLimit,
            IsComplete:          isComplete,
            WasAlreadyBuilt:     false);
    }

    // ── Media helpers ─────────────────────────────────────────────────────────

    private static IEnumerable<MediaEntry> EnumerateMedia(ScreenScraperResult r)
    {
        var img = ScreenScraperClient.ValidImageExts;
        var vid = ScreenScraperClient.ValidVideoExts;
        var doc = ScreenScraperClient.ValidDocumentExts;

        int i = 0;
        foreach (var x in r.TitleScreenshots)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("screenshot-title", "", img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.GameplayScreenshots)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("screenshot", "", img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.Fanart)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("fanart", "", img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.LogosHd)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("logo-hd", "", img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.Logos)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("logo", "", img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.Marquees)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("marquee", "", img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.Flyers)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("flyer", "", img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.Manuals)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("manual", "", doc, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.CoverFront)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("cover-front", x.Region, img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.CoverBack)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("cover-back", x.Region, img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.CoverSpine)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("cover-spine", x.Region, img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.CoverWrap)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("cover-wrap", x.Region, img, x.Url, x.Format, x.Size, i++);

        if (r.Video is not null && !string.IsNullOrEmpty(r.Video.Url))
            yield return new MediaEntry("video", "", vid, r.Video.Url, r.Video.Format, r.Video.Size, 0);

        i = 0;
        foreach (var x in r.PhysicalMedia)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("physical-media", "", img, x.Url, x.Format, x.Size, i++);

        i = 0;
        foreach (var x in r.PhysicalTexture)
            if (!string.IsNullOrEmpty(x.Url))
                yield return new MediaEntry("physical-texture", "", img, x.Url, x.Format, x.Size, i++);
    }

    private async Task<MediaDownloadResult> DownloadGameMediaAsync(
        ScreenScraperResult result, string gameId,
        string stagingDir, bool force,
        IMediaDownloader downloader,
        CancellationToken ct)
    {
        int written = 0, alreadyStaged = 0, failed = 0;

        foreach (var entry in EnumerateMedia(result))
        {
            ct.ThrowIfCancellationRequested();

            var typeDir  = Path.Combine(stagingDir, "media", entry.Type);
            Directory.CreateDirectory(typeDir);

            var stemName = string.IsNullOrEmpty(entry.Region)
                ? $"{gameId}_{entry.Index}"
                : $"{gameId}_{entry.Region}_{entry.Index}";
            var stemPath = Path.Combine(typeDir, stemName);

            var existing = Directory.EnumerateFiles(typeDir, stemName + ".*")
                .Where(f => new FileInfo(f).Length > 0)
                .ToList();

            if (existing.Count > 0)
            {
                if (!force)
                {
                    alreadyStaged++;
                    continue;
                }
                foreach (var f in existing)
                    try { File.Delete(f); } catch { }
            }

            try
            {
                var downloaded = await downloader.DownloadAsync(
                    entry.Url, stemPath, entry.Format, entry.ValidExts, entry.Size, ct);
                if (downloaded is not null) written++;
                else failed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (ScreenScraperRateLimitException)
            {
                return new MediaDownloadResult(written, alreadyStaged, failed, HitRateLimit: true);
            }
            catch
            {
                failed++;
            }
        }

        return new MediaDownloadResult(written, alreadyStaged, failed, HitRateLimit: false);
    }

    private static int CountStagedMediaForGame(string stagingDir, string gameId)
    {
        var mediaDir = Path.Combine(stagingDir, "media");
        if (!Directory.Exists(mediaDir)) return 0;
        int count = 0;
        foreach (var typeDir in Directory.EnumerateDirectories(mediaDir))
            count += Directory.EnumerateFiles(typeDir, $"{gameId}_*").Count();
        return count;
    }

    private static Dictionary<string, int> CollectMediaByType(string mediaDir)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!Directory.Exists(mediaDir)) return result;
        foreach (var typeDir in Directory.EnumerateDirectories(mediaDir))
        {
            var type = Path.GetFileName(typeDir);
            result[type] = Directory.EnumerateFiles(typeDir).Count();
        }
        return result;
    }

    // ── CSV parser ────────────────────────────────────────────────────────────

    private (List<(string GameId, string Title)> Games, int SkippedRows) ParseCsv(string csvPath)
    {
        var games       = new List<(string, string)>();
        int skippedRows = 0;

        using var reader = new StreamReader(csvPath, Encoding.UTF8);
        string? line;
        bool first = true;

        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            var m = CsvLineRx.Match(line);
            if (!m.Success) { skippedRows++; continue; }

            var id    = m.Groups["id"].Value.Trim();
            var title = m.Groups["name"].Value.Trim();

            if (first)
            {
                first = false;
                if (string.Equals(id, "Game ID", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title))
            {
                skippedRows++;
                continue;
            }

            games.Add((id, title));
        }

        return (games, skippedRows);
    }

    // ── Manifest / progress / ZIP helpers ─────────────────────────────────────

    private static string BuildManifest(
        ScreenScraperCachePackageBuildOptions options,
        int payloadCount,
        int mediaCount,
        Dictionary<string, int> mediaByType)
    {
        var manifest = new
        {
            version         = 1,
            provider        = ArkadiaProviders.ScreenScraper,
            cacheProviderId = ArkadiaProviders.ScreenScraperCache,
            systemId        = options.SystemId,
            systemName      = options.SystemName,
            builtAtUtc      = DateTime.UtcNow.ToString("o"),
            gameCount       = payloadCount,
            payloadCount,
            mediaCount,
            mediaCountByType = mediaByType,
            mediaTypes       = mediaByType.Keys.OrderBy(k => k).ToArray(),
        };
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void WriteProgress(
        string stagingDir,
        int validGames, int payloadsWritten, int alreadyStaged,
        int skippedRows, int failedFetches,
        int mediaWritten, int alreadyStagedMedia, int failedMediaDownloads,
        bool hitRateLimit, bool hitSafeLimit, string lastGameId)
    {
        var prog = new
        {
            validGames,
            payloadsWritten,
            alreadyStaged,
            payloadsAvailable = payloadsWritten + alreadyStaged,
            skippedRows,
            failedFetches,
            mediaWritten,
            alreadyStagedMedia,
            failedMediaDownloads,
            hitRateLimit,
            hitSafeLimit,
            lastGameId,
            updatedAt = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(
            Path.Combine(stagingDir, "progress.json"),
            JsonSerializer.Serialize(prog, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    private static void CreateZip(string zipPath, string stagingDir, string payloadsDir, string manifestJson)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var mEntry = zip.CreateEntry(ScreenScraperCachePackageLayout.ManifestEntry, CompressionLevel.Optimal);
        using (var w = new StreamWriter(mEntry.Open(), Encoding.UTF8))
            w.Write(manifestJson);

        AddFileToZip(zip, Path.Combine(stagingDir, ScreenScraperCachePackageLayout.GamesListEntry),
            ScreenScraperCachePackageLayout.GamesListEntry);

        foreach (var file in Directory.EnumerateFiles(payloadsDir, "*.json"))
        {
            var rawJson   = File.ReadAllText(file, Encoding.UTF8);
            var sanitized = ScreenScraperPayloadSanitizer.SanitizeJson(rawJson);
            var pe        = zip.CreateEntry(
                ScreenScraperCachePackageLayout.PayloadsPrefix + Path.GetFileName(file),
                CompressionLevel.Optimal);
            using var pw  = new StreamWriter(pe.Open(), Encoding.UTF8);
            pw.Write(sanitized);
        }

        var mediaDir = Path.Combine(stagingDir, "media");
        if (Directory.Exists(mediaDir))
        {
            foreach (var typeDir in Directory.EnumerateDirectories(mediaDir))
            {
                var type = Path.GetFileName(typeDir);
                foreach (var file in Directory.EnumerateFiles(typeDir))
                    AddFileToZip(zip, file,
                        $"{ScreenScraperCachePackageLayout.MediaPrefix}{type}/{Path.GetFileName(file)}");
            }
        }
    }

    private static void AddFileToZip(ZipArchive zip, string filePath, string entryName)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var src = File.OpenRead(filePath);
        using var dst = entry.Open();
        src.CopyTo(dst);
    }
}
