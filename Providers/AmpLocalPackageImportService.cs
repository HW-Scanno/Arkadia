using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Library;

namespace Arkadia.Providers;

public sealed class AmpLocalPackageImportService(string dataDir)
{
    private const string ProviderId = "arkadia-media-pack";

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

    private static readonly HashSet<string> CoverMediaTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cover-front", "cover-back", "cover-spine", "cover-wrap",
        };

    public Task<AmpImportSummary> ImportAsync(
        LibraryEntry                             entry,
        string                                   ampFilePath,
        AmpReleaseInfo                           release,
        IReadOnlyList<MetadataValueMappingRecord> mappings,
        bool                                     autoApplyEmptyFields  = false,
        bool                                     extractMedia          = true,
        bool                                     respectExcludedMedia  = true,
        bool                                     skipExistingMedia     = true,
        AmpReleaseMatchKind                      matchKind             = AmpReleaseMatchKind.None,
        IProgress<string>?                       progress              = null,
        CancellationToken                        ct                    = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(ampFilePath))
            throw new ArgumentException("AMP file path must not be empty.", nameof(ampFilePath));
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(mappings);

        return Task.Run(() => ImportCore(
            entry, ampFilePath, release, mappings,
            autoApplyEmptyFields, extractMedia, respectExcludedMedia, skipExistingMedia,
            matchKind, progress, ct), ct);
    }

    // ── Core (runs on thread pool) ────────────────────────────────────────────

    private AmpImportSummary ImportCore(
        LibraryEntry                             entry,
        string                                   ampFilePath,
        AmpReleaseInfo                           release,
        IReadOnlyList<MetadataValueMappingRecord> mappings,
        bool                                     autoApplyEmptyFields,
        bool                                     extractMedia,
        bool                                     respectExcludedMedia,
        bool                                     skipExistingMedia,
        AmpReleaseMatchKind                      matchKind,
        IProgress<string>?                       progress,
        CancellationToken                        ct)
    {
        ct.ThrowIfCancellationRequested();

        var store = new DatLineStore(entry.DbPath);

        // ── 1. Metadata proposals ─────────────────────────────────────────────

        var all     = store.LoadReleaseMetadata();
        var current = all.TryGetValue(entry.ReleaseId, out var loaded)
            ? loaded
            : new ReleaseMetadataRecord { ReleaseId = entry.ReleaseId };

        var proposed = BuildProposals(release, mappings);

        var (_, autoApplied) = store.ApplyProviderProposals(
            entry.ReleaseId, ProviderId, proposed, current, autoApplyEmptyFields);

        if (proposed.Count > 0)
            progress?.Report("Proposed metadata from AMP.");

        // ── 2. Media extraction ───────────────────────────────────────────────

        int mediaExtracted  = 0;
        int skippedExcluded = 0;
        int skippedExisting = 0;
        int failedSha256    = 0;
        var mediaByType     = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (!extractMedia || release.Media.Count == 0)
        {
            return new AmpImportSummary(
                ProposalsSaved:            proposed.Count > 0,
                MetadataFieldsProposed:    proposed.Count,
                MetadataFieldsApplied:     autoApplied.Count,
                MediaFilesExtracted:       0,
                MediaFilesSkippedExcluded: 0,
                MediaFilesSkippedExisting: 0,
                MediaFilesFailedSha256:    0,
                MatchKind:                 matchKind,
                MediaByType:               mediaByType);
        }

        MediaStore.EnsureMediaFolders(dataDir, entry.HardwareFamilyId, entry.DatLineId);

        var localRows      = store.LoadMediaCurationRows(entry.ReleaseId);
        var excludedHashes = respectExcludedMedia
            ? BuildExcludedHashSet(localRows)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ampReader = new AmpPackageReaderService();

        foreach (var media in release.Media)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(media.Sha256))
            {
                failedSha256++;
                continue;
            }

            if (respectExcludedMedia && excludedHashes.Contains(media.Sha256))
            {
                progress?.Report($"Skipped {media.MediaType}: excluded locally.");
                skippedExcluded++;
                continue;
            }

            if (skipExistingMedia && localRows.Any(r =>
                    string.Equals(r.FileSha256, media.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                progress?.Report($"Skipped {media.MediaType}: already present.");
                skippedExisting++;
                continue;
            }

            var ext = Path.GetExtension(media.FileName);
            if (ext.Length == 0) ext = Path.GetExtension(media.ArchivePath);
            if (ext.Length == 0) { failedSha256++; continue; }

            var canonical = MediaStore.NormalizeMediaType(media.MediaType);
            if (!MediaTypeToFolder.TryGetValue(canonical, out var folder))
            {
                failedSha256++;
                continue;
            }

            var destPath = CoverMediaTypes.Contains(canonical)
                ? MediaStore.NextIndexedCoverStem(
                      dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name, folder, "wor")
                  + ext
                : MediaStore.NextIndexedMediaPath(
                      dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name, folder, ext);

            var tempPath = Path.GetTempFileName();
            try
            {
                // Stream AMP entry → temp file
                try
                {
                    using var src  = ampReader.OpenMediaStream(ampFilePath, media.ArchivePath);
                    using var dest = new FileStream(
                        tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    src.CopyTo(dest);
                }
                catch (FileNotFoundException)
                {
                    progress?.Report($"Failed {canonical}: entry not found in AMP.");
                    failedSha256++;
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    progress?.Report($"Failed {canonical}: {ex.Message}");
                    failedSha256++;
                    continue;
                }

                ct.ThrowIfCancellationRequested();

                // Verify temp SHA-256 matches AMP entry
                var tempHash = ReleaseMediaCurationService.ComputeSha256(tempPath);
                if (!string.Equals(tempHash, media.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report($"Failed {canonical}: SHA-256 mismatch.");
                    failedSha256++;
                    continue;
                }

                // Move temp to final destination
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                try
                {
                    File.Move(tempPath, destPath, overwrite: false);
                    tempPath = "";
                }
                catch (IOException)
                {
                    try
                    {
                        File.Copy(tempPath, destPath, overwrite: false);
                        File.Delete(tempPath);
                        tempPath = "";
                    }
                    catch (Exception ex2) when (ex2 is not OperationCanceledException)
                    {
                        progress?.Report($"Failed {canonical}: write to destination failed.");
                        failedSha256++;
                        continue;
                    }
                }

                // Upsert curation row (IsPreferred = false; preferred handled below)
                store.UpsertMediaCurationRow(new MediaCurationRow(
                    ReleaseId:      entry.ReleaseId,
                    MediaType:      canonical,
                    FilePath:       destPath,
                    FileSha256:     media.Sha256,
                    IsPreferred:    false,
                    IsExcluded:     false,
                    ExcludedReason: null,
                    Credits:        string.IsNullOrWhiteSpace(media.Credits) ? null : media.Credits,
                    Notes:          null));

                // Preferred: honor AMP flag only when no local preferred already exists
                if (media.Preferred)
                {
                    var freshRows = store.LoadMediaCurationRows(entry.ReleaseId);
                    bool hasOtherPreferred = freshRows.Any(r =>
                        string.Equals(r.MediaType, canonical, StringComparison.OrdinalIgnoreCase) &&
                        r.IsPreferred &&
                        !string.Equals(r.FilePath, destPath, StringComparison.OrdinalIgnoreCase));
                    if (!hasOtherPreferred)
                        store.SetPreferredMediaCuration(entry.ReleaseId, canonical, destPath);
                }

                mediaExtracted++;
                mediaByType[canonical] = mediaByType.GetValueOrDefault(canonical) + 1;
                progress?.Report($"Extracted {canonical}.");

                localRows = store.LoadMediaCurationRows(entry.ReleaseId);
                if (respectExcludedMedia)
                    excludedHashes = BuildExcludedHashSet(localRows);
            }
            finally
            {
                if (tempPath.Length > 0)
                    try { File.Delete(tempPath); } catch { }
            }
        }

        return new AmpImportSummary(
            ProposalsSaved:            proposed.Count > 0,
            MetadataFieldsProposed:    proposed.Count,
            MetadataFieldsApplied:     autoApplied.Count,
            MediaFilesExtracted:       mediaExtracted,
            MediaFilesSkippedExcluded: skippedExcluded,
            MediaFilesSkippedExisting: skippedExisting,
            MediaFilesFailedSha256:    failedSha256,
            MatchKind:                 matchKind,
            MediaByType:               mediaByType);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, string> BuildProposals(
        AmpReleaseInfo                           release,
        IReadOnlyList<MetadataValueMappingRecord> mappings)
    {
        var proposed = new Dictionary<string, string>(StringComparer.Ordinal);

        void Propose(string field, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                proposed[field] = value.Trim();
        }

        string Norm(string field, string value) =>
            MetadataValueNormalizer.Normalize(field, value, mappings);

        Propose("title",            release.Title);
        Propose("original_title",   release.OriginalTitle);
        Propose("sort_title",       release.SortTitle);
        Propose("developer",        release.Developer);
        Propose("publisher",        release.Publisher);
        Propose("year",             release.Year);
        Propose("languages",        release.Languages);
        Propose("alternate_titles", release.AlternateTitles);
        Propose("description",      release.Description);
        Propose("genre",            Norm("genre",    release.Genre));
        Propose("subgenre",         Norm("subgenre", release.Subgenre));
        Propose("players",          Norm("players",  release.Players));
        Propose("release_type",     release.ReleaseType);
        Propose("rating",           Norm("rating",   release.Rating));

        return proposed;
    }

    private static HashSet<string> BuildExcludedHashSet(IReadOnlyList<MediaCurationRow> rows) =>
        rows.Where(r => r.IsExcluded && r.FileSha256 is { Length: > 0 })
            .Select(r => r.FileSha256!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

// ── Result record ─────────────────────────────────────────────────────────────

public sealed record AmpImportSummary(
    bool                             ProposalsSaved,
    int                              MetadataFieldsProposed,
    int                              MetadataFieldsApplied,
    int                              MediaFilesExtracted,
    int                              MediaFilesSkippedExcluded,
    int                              MediaFilesSkippedExisting,
    int                              MediaFilesFailedSha256,
    AmpReleaseMatchKind              MatchKind,
    IReadOnlyDictionary<string, int> MediaByType);
