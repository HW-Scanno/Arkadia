using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Arkadia.Data;

namespace Arkadia;

public sealed class AmpExportPlanService(string dataDir, CatalogService catalog)
{
    public AmpExportPlan PlanExport(
        string            hardwareFamilyId,
        string            datLineId,
        CancellationToken ct = default)
    {
        var datLine = FindDatLine(datLineId);
        var dbPath  = Path.Combine(dataDir, datLine.DataStorePath);
        if (!File.Exists(dbPath))
            throw new InvalidOperationException(
                $"DAT line database not found at '{dbPath}'.");

        var store    = new DatLineStore(dbPath);
        var releases = store.LoadReleasesByDatLine(datLineId);
        var allMeta  = store.LoadReleaseMetadata();

        var planReleases = new List<AmpExportPlanRelease>(releases.Count);

        // Cross-release duplicate tracking
        var archivePathsSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashesSeen       = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int releasesWithMetadata = 0;
        int releasesWithMedia    = 0;
        int totalMediaFiles      = 0;
        long totalBytes          = 0;
        int exclusionCount       = 0;
        int extraNotesCount      = 0;

        foreach (var release in releases)
        {
            ct.ThrowIfCancellationRequested();

            var releaseIssues = new List<AmpExportPlanIssue>();
            allMeta.TryGetValue(release.Id, out var meta);
            bool   hasMetadata   = meta is { Title.Length: > 0 };
            string title          = meta?.Title          ?? "";
            string originalTitle  = meta?.OriginalTitle  ?? "";
            string sortTitle      = meta?.SortTitle      ?? "";
            string developer      = meta?.Developer      ?? "";
            string publisher      = meta?.Publisher      ?? "";
            string year           = meta?.Year           ?? "";
            string languages      = meta?.Languages      ?? "";
            string alternateTitles = meta?.AlternateTitles ?? "";
            string description    = meta?.Description    ?? "";
            string genre          = meta?.Genre          ?? "";
            string subgenre       = meta?.Subgenre       ?? "";
            string players        = meta?.Players        ?? "";
            string releaseType    = meta?.ReleaseType    ?? "";
            string rating         = meta?.Rating         ?? "";

            if (meta is not null && !hasMetadata)
                releaseIssues.Add(new AmpExportPlanIssue(
                    AmpExportPlanSeverity.Warning, "metadata",
                    $"Release '{release.Name}' has a metadata row but no title."));

            var curationRows    = store.LoadMediaCurationRows(release.Id);
            var mediaEntries    = new List<AmpExportPlanMediaEntry>();
            var exclusionHashes = new List<string>();
            bool hasNoCoverFront = true;

            foreach (var row in curationRows)
            {
                if (row.IsExcluded)
                {
                    exclusionCount++;
                    if (row.FileSha256 is { Length: > 0 } hash)
                        exclusionHashes.Add(hash);
                    else
                        releaseIssues.Add(new AmpExportPlanIssue(
                            AmpExportPlanSeverity.Warning, "exclusion",
                            $"Excluded asset '{Path.GetFileName(row.FilePath)}' has no stored SHA-256 — " +
                            "cannot guarantee future reintroduction prevention."));
                    continue;
                }

                if (!File.Exists(row.FilePath))
                {
                    releaseIssues.Add(new AmpExportPlanIssue(
                        AmpExportPlanSeverity.Warning, "media",
                        $"File not found on disk: '{Path.GetFileName(row.FilePath)}' — skipped."));
                    continue;
                }

                var fi = new FileInfo(row.FilePath);
                if (fi.Length == 0)
                {
                    releaseIssues.Add(new AmpExportPlanIssue(
                        AmpExportPlanSeverity.Error, "media",
                        $"Zero-byte file: '{Path.GetFileName(row.FilePath)}' — skipped."));
                    continue;
                }

                var computedSha256 = ReleaseMediaCurationService.ComputeSha256(row.FilePath);
                if (computedSha256 is null)
                {
                    releaseIssues.Add(new AmpExportPlanIssue(
                        AmpExportPlanSeverity.Error, "media",
                        $"Could not compute SHA-256 for '{Path.GetFileName(row.FilePath)}' — skipped."));
                    continue;
                }

                if (row.FileSha256 is { Length: > 0 } stored &&
                    !string.Equals(stored, computedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    releaseIssues.Add(new AmpExportPlanIssue(
                        AmpExportPlanSeverity.Error, "media",
                        $"SHA-256 mismatch for '{Path.GetFileName(row.FilePath)}': " +
                        $"stored={stored[..8]}…, computed={computedSha256[..8]}… — skipped."));
                    continue;
                }

                var archivePath = AmpReportHelpers.BuildArchivePath(row.MediaType, release.Id, row.FilePath);
                if (archivePathsSeen.TryGetValue(archivePath, out var conflictId))
                {
                    releaseIssues.Add(new AmpExportPlanIssue(
                        AmpExportPlanSeverity.Error, "archive",
                        $"Duplicate archive path '{archivePath}' conflicts with release " +
                        $"'{conflictId}' — skipped."));
                    continue;
                }
                archivePathsSeen[archivePath] = release.Id;

                if (hashesSeen.TryGetValue(computedSha256, out var hashConflictId))
                    releaseIssues.Add(new AmpExportPlanIssue(
                        AmpExportPlanSeverity.Warning, "dedup",
                        $"SHA-256 of '{Path.GetFileName(row.FilePath)}' duplicates a file " +
                        $"already seen in release '{hashConflictId}'."));
                else
                    hashesSeen[computedSha256] = release.Id;

                if (row.MediaType == "cover-front")
                    hasNoCoverFront = false;

                mediaEntries.Add(new AmpExportPlanMediaEntry(
                    MediaType:   row.MediaType,
                    FilePath:    row.FilePath,
                    Sha256:      computedSha256,
                    SizeBytes:   fi.Length,
                    IsPreferred: row.IsPreferred,
                    Credits:     row.Credits));

                totalMediaFiles++;
                totalBytes += fi.Length;
            }

            if (hasMetadata && hasNoCoverFront)
                releaseIssues.Add(new AmpExportPlanIssue(
                    AmpExportPlanSeverity.Warning, "media",
                    $"Release '{release.Name}' has metadata but no front cover."));

            var extraNotes = store.GetReleaseExtraNotes(release.Id);
            if (extraNotes is { Length: > 0 }) extraNotesCount++;

            if (hasMetadata)      releasesWithMetadata++;
            if (mediaEntries.Count > 0) releasesWithMedia++;

            planReleases.Add(new AmpExportPlanRelease(
                ReleaseId:       release.Id,
                DatName:         release.Name,
                Title:           title,
                OriginalTitle:   originalTitle,
                SortTitle:       sortTitle,
                Developer:       developer,
                Publisher:       publisher,
                Year:            year,
                Languages:       languages,
                AlternateTitles: alternateTitles,
                Description:     description,
                Genre:           genre,
                Subgenre:        subgenre,
                Players:         players,
                ReleaseType:     releaseType,
                Rating:          rating,
                HasMetadata:     hasMetadata,
                MediaEntries:    mediaEntries,
                ExclusionHashes: exclusionHashes,
                ExtraNotes:      extraNotes,
                Issues:          releaseIssues));
        }

        return new AmpExportPlan(
            HardwareFamilyId:     hardwareFamilyId,
            DatLineId:            datLineId,
            SystemName:           datLine.Name,
            ReleaseCount:         releases.Count,
            ReleasesWithMetadata: releasesWithMetadata,
            ReleasesWithMedia:    releasesWithMedia,
            TotalMediaFiles:      totalMediaFiles,
            TotalBytes:           totalBytes,
            ExclusionCount:       exclusionCount,
            ExtraNotesCount:      extraNotesCount,
            Releases:             planReleases,
            Issues:               []);
    }

    private DatLineRecord FindDatLine(string datLineId)
    {
        var datLine = catalog.LoadDatLines()
            .Find(dl => string.Equals(dl.Id, datLineId, StringComparison.Ordinal));
        if (datLine is null)
            throw new InvalidOperationException($"DAT line '{datLineId}' not found in catalog.");
        return datLine;
    }
}
