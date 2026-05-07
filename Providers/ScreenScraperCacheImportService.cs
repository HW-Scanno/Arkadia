using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Library;
using Microsoft.Data.Sqlite;

namespace Arkadia.Providers;

public sealed record CacheImportSummary(
    bool PayloadImported,
    bool ProposalsSaved,
    int  MediaExtracted,
    IReadOnlyDictionary<string, int> MediaByType);

/// <summary>
/// Offline import: reads payload + media from an indexed cache ZIP package,
/// saves provider proposals (provider="screenscraper-cache"), and extracts media files.
/// No network calls are made.
/// </summary>
public sealed class ScreenScraperCacheImportService(string dataDir, CatalogService catalog)
{
    private static readonly Dictionary<string, string> MediaTypeToFolder =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cover-front"]      = "covers-front",
            ["cover-back"]       = "covers-back",
            ["cover-spine"]      = "covers-spine",
            ["cover-wrap"]       = "covers-wrap",
            ["screenshot-title"] = "screenshots-title",
            ["screenshot"]       = "screenshots",
            ["fanart"]           = "fanart",
            ["video"]            = "videos",
            ["logo-hd"]          = "logos-hd",
            ["logo"]             = "logos",
            ["marquee"]          = "marquees",
            ["flyer"]            = "flyers",
            ["manual"]           = "manuals",
            ["physical"]         = "physical",
            ["physical-texture"] = "physical-texture",
        };

    private static readonly HashSet<string> CoverFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "covers-front", "covers-back", "covers-spine", "covers-wrap",
        };

    public const string ProviderId = ArkadiaProviders.ScreenScraperCache;

    public async Task<CacheImportSummary> ImportAsync(
        LibraryEntry entry,
        ScreenScraperCacheCandidate candidate,
        IReadOnlyList<MetadataValueMappingRecord> mappings,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // ── 1. Open ZIP, locate and read payload ─────────────────────────────
        progress?.Report("Opening cache package…");

        using var zip = ZipFile.OpenRead(candidate.PackagePath);

        var payloadZipEntry = FindPayloadEntry(zip, candidate);
        if (payloadZipEntry is null)
            throw new InvalidDataException(
                $"Payload for game {candidate.ProviderGameId} not found in cache package.");

        string payloadJson;
        using (var stream = payloadZipEntry.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            payloadJson = await reader.ReadToEndAsync(ct);

        // ── 2. Parse JSON ─────────────────────────────────────────────────────
        var result = ScreenScraperClient.ParseGameJson(payloadJson);
        if (result is null)
            throw new InvalidDataException(
                $"Failed to parse payload JSON for game {candidate.ProviderGameId}.");

        var store = new DatLineStore(entry.DbPath);

        // ── 3. Save provider payload ──────────────────────────────────────────
        progress?.Report("Saving provider payload…");
        store.SaveProviderPayload(entry.ReleaseId, ProviderId, payloadJson);

        // ── 4. Write metadata JSON file ───────────────────────────────────────
        var metaDir = Path.Combine(
            MediaStore.DatLinePath(dataDir, entry.HardwareFamilyId, entry.DatLineId),
            "metadata");
        Directory.CreateDirectory(metaDir);
        var metaFile = Path.Combine(metaDir,
            $"{MediaStore.ReleaseStem(entry.Name)}_{ProviderId}.json");
        await File.WriteAllTextAsync(metaFile, payloadJson, ct);

        // ── 5. Build + save proposals ─────────────────────────────────────────
        var proposed = BuildProposals(result, mappings);
        var current  = entry.Metadata ?? new ReleaseMetadataRecord { ReleaseId = entry.ReleaseId };

        progress?.Report("Saving metadata proposals…");
        store.ApplyProviderProposals(entry.ReleaseId, ProviderId, proposed, current,
            autoApplyEmptyFields: false);

        // ── 6. Extract media from ZIP ─────────────────────────────────────────
        progress?.Report("Extracting media from cache…");
        MediaStore.EnsureMediaFolders(dataDir, entry.HardwareFamilyId, entry.DatLineId);

        var (mediaExtracted, mediaByType) =
            await ExtractMediaAsync(zip, entry, candidate.ProviderGameId, ct);

        return new CacheImportSummary(
            PayloadImported: true,
            ProposalsSaved:  proposed.Count > 0,
            MediaExtracted:  mediaExtracted,
            MediaByType:     mediaByType);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private ZipArchiveEntry? FindPayloadEntry(ZipArchive zip, ScreenScraperCacheCandidate candidate)
    {
        var dbEntry = LookupPayloadZipEntry(candidate);
        if (dbEntry is { Length: > 0 })
        {
            var e = zip.GetEntry(dbEntry);
            if (e is not null) return e;
        }
        return zip.GetEntry(ScreenScraperCachePackageLayout.PayloadEntry(candidate.ProviderGameId));
    }

    private string? LookupPayloadZipEntry(ScreenScraperCacheCandidate candidate)
    {
        using var conn = new SqliteConnection($"Data Source={catalog.DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT payload_zip_entry FROM cache_package_games
            WHERE package_id = $pkgId AND provider_game_id = $gameId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$pkgId",  candidate.PackageId);
        cmd.Parameters.AddWithValue("$gameId", candidate.ProviderGameId);
        return cmd.ExecuteScalar() as string;
    }

    private static Dictionary<string, string> BuildProposals(
        ScreenScraperResult result,
        IReadOnlyList<MetadataValueMappingRecord> mappings)
    {
        var proposed = new Dictionary<string, string>(StringComparer.Ordinal);
        void Propose(string field, string value)
            { if (value.Length > 0) proposed[field] = value; }
        string Norm(string field, string value)
            => MetadataValueNormalizer.Normalize(field, value, mappings);

        Propose("title",          result.Title);
        Propose("original_title", result.OriginalTitle);
        Propose("developer",      result.Developer);
        Propose("publisher",      result.Publisher);
        Propose("year",           result.Year);
        Propose("languages",      result.Languages);
        Propose("description",    result.Description);
        Propose("genre",          Norm("genre",    result.Genre));
        Propose("subgenre",       Norm("subgenre", result.Subgenre));
        Propose("players",        Norm("players",  result.Players));
        Propose("rating",         Norm("rating",   result.Rating));

        return proposed;
    }

    private async Task<(int Total, Dictionary<string, int> ByType)> ExtractMediaAsync(
        ZipArchive zip, LibraryEntry entry, string gameId, CancellationToken ct)
    {
        var mediaRows = LoadMediaRows(gameId);

        int total  = 0;
        var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in mediaRows)
        {
            ct.ThrowIfCancellationRequested();

            var canonical = MediaStore.NormalizeMediaType(row.MediaType);
            if (!MediaTypeToFolder.TryGetValue(canonical, out var folder)) continue;

            var zipEntry = zip.GetEntry(row.ZipEntry);
            if (zipEntry is null) continue;

            var ext = row.FileExt.Length > 0 ? row.FileExt : Path.GetExtension(row.ZipEntry);
            if (ext.Length > 0 && !ext.StartsWith('.')) ext = "." + ext;
            if (ext.Length == 0) continue;

            string destPath;
            if (CoverFolders.Contains(folder))
            {
                var region = row.Region.Length > 0 ? row.Region : "wor";
                var stem   = MediaStore.NextIndexedCoverStem(
                    dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name, folder, region);
                destPath = stem + ext;
            }
            else
            {
                var stem = MediaStore.NextIndexedMediaStem(
                    dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name, folder);
                destPath = stem + ext;
            }

            // Skip if a non-empty file already exists at this path
            if (File.Exists(destPath) && new FileInfo(destPath).Length > 0) continue;

            try
            {
                using var src  = zipEntry.Open();
                using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dest, ct);

                total++;
                byType[canonical] = byType.GetValueOrDefault(canonical) + 1;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* swallow per-file errors */ }
        }

        return (total, byType);
    }

    private List<MediaRowRecord> LoadMediaRows(string gameId)
    {
        using var conn = new SqliteConnection($"Data Source={catalog.DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT media_type, region, zip_entry, file_ext
            FROM cache_package_media
            WHERE provider_game_id = $gameId
            """;
        cmd.Parameters.AddWithValue("$gameId", gameId);

        var rows = new List<MediaRowRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new MediaRowRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }
        return rows;
    }

    private sealed record MediaRowRecord(
        string MediaType, string Region, string ZipEntry, string FileExt);

    // ── Batch import (non-interactive) ────────────────────────────────────────

    public async Task<BulkImportResult> BatchImportAsync(
        LibraryEntry                              entry,
        ScreenScraperCacheCandidate               candidate,
        BulkImportOptions                         options,
        IReadOnlyList<MetadataValueMappingRecord> mappings,
        IReadOnlySet<string>                      excludedHashes,
        CancellationToken                         ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var zip = ZipFile.OpenRead(candidate.PackagePath);

        var payloadZipEntry = FindPayloadEntry(zip, candidate);
        if (payloadZipEntry is null)
            throw new InvalidDataException(
                $"Payload for game {candidate.ProviderGameId} not found in cache package.");

        string payloadJson;
        using (var stream = payloadZipEntry.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            payloadJson = await reader.ReadToEndAsync(ct);

        var result = ScreenScraperClient.ParseGameJson(payloadJson);
        if (result is null)
            throw new InvalidDataException(
                $"Failed to parse payload JSON for game {candidate.ProviderGameId}.");

        var store = new DatLineStore(entry.DbPath);
        store.SaveProviderPayload(entry.ReleaseId, ProviderId, payloadJson);

        var metaDir = Path.Combine(
            MediaStore.DatLinePath(dataDir, entry.HardwareFamilyId, entry.DatLineId),
            "metadata");
        Directory.CreateDirectory(metaDir);
        var metaFile = Path.Combine(metaDir,
            $"{MediaStore.ReleaseStem(entry.Name)}_{ProviderId}.json");
        await File.WriteAllTextAsync(metaFile, payloadJson, ct);

        var proposed = BuildProposals(result, mappings);
        var current  = entry.Metadata ?? new ReleaseMetadataRecord { ReleaseId = entry.ReleaseId };

        var (merged, autoApplied) = store.ApplyProviderProposals(
            entry.ReleaseId, ProviderId, proposed, current,
            autoApplyEmptyFields: options.AutoApplyEmptyFieldsOnly);

        if (autoApplied.Count > 0)
            entry.Metadata = merged;

        if (!options.ExtractMissingMedia)
            return new BulkImportResult(proposed.Count, autoApplied.Count, 0, 0, 0);

        MediaStore.EnsureMediaFolders(dataDir, entry.HardwareFamilyId, entry.DatLineId);

        var (extracted, skippedExcluded, skippedExisting) =
            await ExtractMediaBatchAsync(zip, entry, candidate.ProviderGameId, options, excludedHashes, ct);

        return new BulkImportResult(proposed.Count, autoApplied.Count, extracted, skippedExcluded, skippedExisting);
    }

    private async Task<(int Extracted, int SkippedExcluded, int SkippedExisting)> ExtractMediaBatchAsync(
        ZipArchive zip, LibraryEntry entry, string gameId,
        BulkImportOptions options, IReadOnlySet<string> excludedHashes,
        CancellationToken ct)
    {
        var mediaRows = LoadMediaRows(gameId);

        int extracted       = 0;
        int skippedExcluded = 0;
        int skippedExisting = 0;

        foreach (var row in mediaRows)
        {
            ct.ThrowIfCancellationRequested();

            var canonical = MediaStore.NormalizeMediaType(row.MediaType);
            if (!MediaTypeToFolder.TryGetValue(canonical, out var folder)) continue;

            var zipEntry = zip.GetEntry(row.ZipEntry);
            if (zipEntry is null) continue;

            var ext = row.FileExt.Length > 0 ? row.FileExt : Path.GetExtension(row.ZipEntry);
            if (ext.Length > 0 && !ext.StartsWith('.')) ext = "." + ext;
            if (ext.Length == 0) continue;

            string destPath;
            if (CoverFolders.Contains(folder))
            {
                var region = row.Region.Length > 0 ? row.Region : "wor";
                var stem   = MediaStore.NextIndexedCoverStem(
                    dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name, folder, region);
                destPath = stem + ext;
            }
            else
            {
                var stem = MediaStore.NextIndexedMediaStem(
                    dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name, folder);
                destPath = stem + ext;
            }

            // Skip before writing to avoid unnecessary IO; FileMode.Create below
            // overwrites unconditionally when OverwriteExistingMedia is true.
            if (!options.OverwriteExistingMedia &&
                File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            {
                skippedExisting++;
                continue;
            }

            try
            {
                using var src  = zipEntry.Open();
                using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dest, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }

            if (options.RespectExcludedMedia)
            {
                var sha256 = ReleaseMediaCurationService.ComputeSha256(destPath);
                if (sha256 is not null && excludedHashes.Contains(sha256))
                {
                    try { File.Delete(destPath); } catch { }
                    skippedExcluded++;
                    continue;
                }
            }

            extracted++;
        }

        return (extracted, skippedExcluded, skippedExisting);
    }
}

// ── Bulk import records ───────────────────────────────────────────────────────

public sealed record BulkImportOptions(
    bool AutoApplyEmptyFieldsOnly,
    bool ExtractMissingMedia,
    bool RespectExcludedMedia,
    bool OverwriteExistingMedia);

public sealed record BulkImportResult(
    int MetadataFieldsProposed,
    int MetadataFieldsApplied,
    int MediaFilesExtracted,
    int MediaFilesSkippedExcluded,
    int MediaFilesSkippedExisting);
