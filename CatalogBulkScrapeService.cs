using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Library;
using Arkadia.Providers;

namespace Arkadia;

// ── Enums ──────────────────────────────────────────────────────────────────────

public enum BulkScrapeScope  { CurrentRelease, MissingOnly, EntireDat }
public enum BulkScrapeStatus { Matched, NoMatch, Ambiguous, Error }

// ── Records ───────────────────────────────────────────────────────────────────

public sealed record BulkScrapeOptions(
    BulkScrapeScope Scope                = BulkScrapeScope.MissingOnly,
    bool AutoApplyEmptyFieldsOnly        = true,
    bool ExtractMissingMedia             = true,
    bool RespectExcludedMedia            = true,
    bool OverwriteExistingMedia          = false);

public sealed record BulkScrapeProgress(
    int    Processed,
    int    Total,
    int    Matched,
    int    NoMatch,
    int    Ambiguous,
    int    Errors,
    string CurrentName);

public sealed record BulkScrapeReleaseResult(
    string           ReleaseId,
    string           ReleaseName,
    BulkScrapeStatus Status,
    string?          ErrorMessage,
    int              MetadataFieldsApplied,
    int              MediaExtracted);

public sealed record BulkScrapeReport(
    IReadOnlyList<BulkScrapeReleaseResult> Results,
    int TotalMatched,
    int TotalNoMatch,
    int TotalAmbiguous,
    int TotalErrors,
    int TotalMetadataApplied,
    int TotalMediaExtracted);

// ── Service ───────────────────────────────────────────────────────────────────

public sealed class CatalogBulkScrapeService(
    string                          dataDir,
    CatalogService                  catalog,
    ScreenScraperCacheImportService importSvc)
{
    private readonly ScreenScraperCacheSearchService _searchSvc = new(catalog);

    public async Task<BulkScrapeReport> RunAsync(
        IReadOnlyList<LibraryEntry>               entries,
        BulkScrapeOptions                         options,
        IReadOnlyList<MetadataValueMappingRecord> mappings,
        IProgress<BulkScrapeProgress>?            progress = null,
        CancellationToken                         ct       = default)
    {
        var results   = new List<BulkScrapeReleaseResult>();
        int total     = entries.Count;
        int processed = 0, matched = 0, noMatch = 0, ambiguous = 0, errors = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var relName = entry.CatalogTitle.Length > 0 ? entry.CatalogTitle : entry.Name;

            progress?.Report(new BulkScrapeProgress(
                processed, total, matched, noMatch, ambiguous, errors, relName));

            var family   = catalog.GetHardwareFamily(entry.HardwareFamilyId);
            var scrapeId = family?.ScrapeSystemId is { Length: > 0 } s ? s : entry.HardwareFamilyId;
            var query    = ScrapeReviewDialog.BuildInitialQuery(entry.CatalogTitle, entry.Name);

            IReadOnlyList<ScreenScraperCacheCandidate> candidates;
            try
            {
                // maxResults=3 detects ties cheaply; ≥2 results → Ambiguous (never auto-applied).
                candidates = _searchSvc.Search(query, scrapeId, maxResults: 3);
            }
            catch (Exception ex)
            {
                results.Add(new BulkScrapeReleaseResult(
                    entry.ReleaseId, relName, BulkScrapeStatus.Error, ex.Message, 0, 0));
                errors++; processed++;
                continue;
            }

            if (candidates.Count == 0)
            {
                results.Add(new BulkScrapeReleaseResult(
                    entry.ReleaseId, relName, BulkScrapeStatus.NoMatch, null, 0, 0));
                noMatch++; processed++;
                continue;
            }

            if (candidates.Count >= 2)
            {
                results.Add(new BulkScrapeReleaseResult(
                    entry.ReleaseId, relName, BulkScrapeStatus.Ambiguous, null, 0, 0));
                ambiguous++; processed++;
                continue;
            }

            var candidate      = candidates[0];
            var excludedHashes = LoadExcludedHashes(entry);
            var bulkOpts       = new BulkImportOptions(
                options.AutoApplyEmptyFieldsOnly,
                options.ExtractMissingMedia,
                options.RespectExcludedMedia,
                options.OverwriteExistingMedia);

            BulkImportResult importResult;
            try
            {
                importResult = await importSvc.BatchImportAsync(
                    entry, candidate, bulkOpts, mappings, excludedHashes, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new BulkScrapeReleaseResult(
                    entry.ReleaseId, relName, BulkScrapeStatus.Error, ex.Message, 0, 0));
                errors++; processed++;
                continue;
            }

            results.Add(new BulkScrapeReleaseResult(
                entry.ReleaseId, relName, BulkScrapeStatus.Matched, null,
                importResult.MetadataFieldsApplied, importResult.MediaFilesExtracted));
            matched++;
            processed++;
        }

        progress?.Report(new BulkScrapeProgress(
            processed, total, matched, noMatch, ambiguous, errors, ""));

        return new BulkScrapeReport(
            results,
            TotalMatched:         matched,
            TotalNoMatch:         noMatch,
            TotalAmbiguous:       ambiguous,
            TotalErrors:          errors,
            TotalMetadataApplied: results.Sum(r => r.MetadataFieldsApplied),
            TotalMediaExtracted:  results.Sum(r => r.MediaExtracted));
    }

    // ── Scope filtering ───────────────────────────────────────────────────────

    public IReadOnlyList<LibraryEntry> FilterEntries(
        IReadOnlyList<LibraryEntry> allEntries,
        BulkScrapeScope             scope,
        LibraryEntry?               selectedEntry)
    {
        return scope switch
        {
            BulkScrapeScope.CurrentRelease => selectedEntry is not null
                ? (IReadOnlyList<LibraryEntry>)[selectedEntry]
                : [],
            BulkScrapeScope.EntireDat      => allEntries,
            _                              => allEntries.Where(x => !IsComplete(x)).ToList(),
        };
    }

    // ── Used by callers to check completion status ────────────────────────────

    public bool IsComplete(LibraryEntry entry)
    {
        if ((entry.Metadata?.QualityScore ?? 0) < 6) return false;
        return HasCoverFront(entry);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool HasCoverFront(LibraryEntry entry)
    {
        var mediaRoot = MediaStore.DatLinePath(dataDir, entry.HardwareFamilyId, entry.DatLineId);
        var coverDir  = Path.Combine(mediaRoot, "covers-front");
        if (!Directory.Exists(coverDir)) return false;
        var stem = MediaStore.ReleaseStem(entry.Name) + "_";
        return Directory.EnumerateFiles(coverDir, stem + "*").Any();
    }

    private static IReadOnlySet<string> LoadExcludedHashes(LibraryEntry entry)
    {
        var store = new DatLineStore(entry.DbPath);
        return store.LoadMediaCurationRows(entry.ReleaseId)
            .Where(r => r.IsExcluded && r.FileSha256 is { Length: > 0 })
            .Select(r => r.FileSha256!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
