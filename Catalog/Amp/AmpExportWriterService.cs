using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Arkadia.Data;

namespace Arkadia;

public sealed class AmpExportWriterService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AmpExportWriteResult Write(
        AmpExportPlan plan,
        string        outputPath,
        bool          overwrite = false,
        CancellationToken ct    = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path must not be empty.", nameof(outputPath));

        if (!outputPath.EndsWith(".amp", StringComparison.OrdinalIgnoreCase))
            outputPath += ".amp";

        if (plan.Releases.Count == 0)
            throw new InvalidOperationException(
                "Cannot write package: plan contains zero releases.");

        var errorCount = AmpReportHelpers.GetErrorCount(plan);
        if (errorCount > 0)
            throw new InvalidOperationException(
                $"Cannot write package: plan contains {errorCount} error(s). " +
                "Resolve all errors before exporting.");

        if (File.Exists(outputPath) && !overwrite)
            throw new InvalidOperationException(
                $"Output file already exists: '{outputPath}'. Pass overwrite=true to replace.");

        var tmpPath = outputPath + ".tmp";

        try
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);

            var parentDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parentDir))
                Directory.CreateDirectory(parentDir);

            // Deterministic ordering: releases by ReleaseId, media by MediaType then FilePath
            var orderedMedia = plan.Releases
                .OrderBy(r => r.ReleaseId, StringComparer.Ordinal)
                .SelectMany(r => r.MediaEntries
                    .OrderBy(e => e.MediaType, StringComparer.Ordinal)
                    .ThenBy(e => e.FilePath, StringComparer.Ordinal)
                    .Select(e => (Release: r, Entry: e)))
                .ToList();

            // Validate all media files before opening the ZIP
            foreach (var (_, entry) in orderedMedia)
            {
                ct.ThrowIfCancellationRequested();
                ValidateMediaEntry(entry);
            }

            // Pre-serialize JSON payloads so bytes are known before building the hash list
            var manifestBytes   = SerializeManifest(plan, orderedMedia.Count);
            var releasesBytes   = SerializeReleases(plan);
            var exclusionsBytes = SerializeExclusions(plan);
            var notesBytes      = SerializeNotes(plan);

            // Build file hash list (files.sha256.json does not include itself)
            var fileHashes = new List<AmpFileHashEntry>
            {
                new("manifest.json",            HashBytes(manifestBytes),   manifestBytes.Length),
                new("releases.json",            HashBytes(releasesBytes),   releasesBytes.Length),
                new("curation/exclusions.json", HashBytes(exclusionsBytes), exclusionsBytes.Length),
                new("curation/notes.json",      HashBytes(notesBytes),      notesBytes.Length),
            };
            foreach (var (rel, entry) in orderedMedia)
            {
                var ap = AmpReportHelpers.BuildArchivePath(entry.MediaType, rel.ReleaseId, entry.FilePath);
                fileHashes.Add(new AmpFileHashEntry(ap, entry.Sha256, entry.SizeBytes));
            }
            var hashesBytes = JsonSerializer.SerializeToUtf8Bytes(fileHashes, JsonOptions);

            // Write ZIP to tmp
            long totalMediaBytes = 0;
            using (var fs  = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteZipEntry(zip, "manifest.json",            manifestBytes);
                WriteZipEntry(zip, "releases.json",            releasesBytes);
                WriteZipEntry(zip, "curation/exclusions.json", exclusionsBytes);
                WriteZipEntry(zip, "curation/notes.json",      notesBytes);
                WriteZipEntry(zip, "hashes/files.sha256.json", hashesBytes);

                foreach (var (rel, entry) in orderedMedia)
                {
                    ct.ThrowIfCancellationRequested();
                    var archivePath = AmpReportHelpers.BuildArchivePath(
                        entry.MediaType, rel.ReleaseId, entry.FilePath);
                    var zipEntry = zip.CreateEntry(archivePath, CompressionLevel.Optimal);
                    using var entryStream = zipEntry.Open();
                    using var fileStream  = File.OpenRead(entry.FilePath);
                    fileStream.CopyTo(entryStream);
                    totalMediaBytes += entry.SizeBytes;
                }
            }

            // Verify the tmp file is non-empty
            var tmpInfo = new FileInfo(tmpPath);
            if (!tmpInfo.Exists || tmpInfo.Length == 0)
                throw new InvalidOperationException("Written package is empty or missing.");

            var packageSha  = ReleaseMediaCurationService.ComputeSha256(tmpPath)
                ?? throw new InvalidOperationException(
                    "Could not compute SHA-256 of the output package.");
            long packageSize = tmpInfo.Length;

            // Atomic commit
            File.Move(tmpPath, outputPath, overwrite: true);

            return new AmpExportWriteResult(
                Success:         true,
                OutputPath:      outputPath,
                PackageBytes:    packageSize,
                Sha256:          packageSha,
                ReleaseCount:    plan.Releases.Count,
                MediaFileCount:  orderedMedia.Count,
                TotalMediaBytes: totalMediaBytes,
                Issues:          []);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static void ValidateMediaEntry(AmpExportPlanMediaEntry entry)
    {
        if (!File.Exists(entry.FilePath))
            throw new InvalidOperationException(
                $"Media file not found: '{entry.FilePath}'");

        var fi = new FileInfo(entry.FilePath);
        if (fi.Length == 0)
            throw new InvalidOperationException(
                $"Zero-byte media file: '{entry.FilePath}'");

        var computed = ReleaseMediaCurationService.ComputeSha256(entry.FilePath);
        if (!string.Equals(computed, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"SHA-256 mismatch for '{Path.GetFileName(entry.FilePath)}': " +
                $"plan={entry.Sha256[..8]}…, computed={computed?[..8]}…");
    }

    // ── JSON serialization ────────────────────────────────────────────────────

    private static byte[] SerializeManifest(AmpExportPlan plan, int mediaFileCount)
    {
        long totalMediaBytes = plan.Releases.Sum(r => r.MediaEntries.Sum(e => e.SizeBytes));
        var obj = new AmpManifestJson(
            FormatName:      "Arkadia Media Pack",
            FormatVersion:   "1",
            CreatedAtUtc:    DateTime.UtcNow.ToString("O"),
            HardwareFamilyId: plan.HardwareFamilyId,
            DatLineId:       plan.DatLineId,
            SystemName:      plan.SystemName,
            ReleaseCount:    plan.Releases.Count,
            MediaFileCount:  mediaFileCount,
            TotalMediaBytes: totalMediaBytes,
            ExclusionCount:  plan.ExclusionCount,
            ExtraNotesCount: plan.ExtraNotesCount,
            Attribution:     new AmpAttributionJson(
                Notice:         AmpAttribution.DefaultNotice,
                GeneralCredits: AmpAttribution.DefaultGeneralCredits));
        return JsonSerializer.SerializeToUtf8Bytes(obj, JsonOptions);
    }

    private static byte[] SerializeReleases(AmpExportPlan plan)
    {
        var releases = plan.Releases
            .OrderBy(r => r.ReleaseId, StringComparer.Ordinal)
            .Select(r => new AmpReleaseJson(
                ReleaseId:       r.ReleaseId,
                DatName:         r.DatName,
                Title:           r.Title,
                OriginalTitle:   r.OriginalTitle,
                SortTitle:       r.SortTitle,
                Developer:       r.Developer,
                Publisher:       r.Publisher,
                Year:            r.Year,
                Languages:       r.Languages,
                AlternateTitles: r.AlternateTitles,
                Description:     r.Description,
                Genre:           r.Genre,
                Subgenre:        r.Subgenre,
                Players:         r.Players,
                ReleaseType:     r.ReleaseType,
                Rating:          r.Rating,
                Media:           r.MediaEntries
                    .OrderBy(e => e.MediaType, StringComparer.Ordinal)
                    .ThenBy(e => e.FilePath, StringComparer.Ordinal)
                    .Select(e => new AmpMediaEntryJson(
                        MediaType:   e.MediaType,
                        ArchivePath: AmpReportHelpers.BuildArchivePath(
                            e.MediaType, r.ReleaseId, e.FilePath),
                        FileName:    Path.GetFileName(e.FilePath),
                        Sha256:      e.Sha256,
                        SizeBytes:   e.SizeBytes,
                        Preferred:   e.IsPreferred,
                        Credits:     e.Credits))
                    .ToList()))
            .ToList();
        return JsonSerializer.SerializeToUtf8Bytes(releases, JsonOptions);
    }

    private static byte[] SerializeExclusions(AmpExportPlan plan)
    {
        var entries = plan.Releases
            .OrderBy(r => r.ReleaseId, StringComparer.Ordinal)
            // MediaType is not tracked at the plan level for exclusion hashes;
            // future versions may carry it per-hash.
            .SelectMany(r => r.ExclusionHashes.Select(h =>
                new AmpExclusionJson(r.ReleaseId, r.DatName, "", h)))
            .ToList();
        return JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
    }

    private static byte[] SerializeNotes(AmpExportPlan plan)
    {
        var entries = plan.Releases
            .Where(r => r.ExtraNotes is { Length: > 0 })
            .OrderBy(r => r.ReleaseId, StringComparer.Ordinal)
            .Select(r => new AmpNoteJson(r.ReleaseId, r.DatName, r.ExtraNotes!))
            .ToList();
        return JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string HashBytes(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }

    private static void WriteZipEntry(ZipArchive zip, string entryPath, byte[] content)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }
}

// ── JSON models (serialization only) ─────────────────────────────────────────

internal sealed record AmpManifestJson(
    string             FormatName,
    string             FormatVersion,
    string             CreatedAtUtc,
    string             HardwareFamilyId,
    string             DatLineId,
    string             SystemName,
    int                ReleaseCount,
    int                MediaFileCount,
    long               TotalMediaBytes,
    int                ExclusionCount,
    int                ExtraNotesCount,
    AmpAttributionJson Attribution);

internal sealed record AmpAttributionJson(
    string Notice,
    string GeneralCredits);

internal sealed record AmpReleaseJson(
    string                           ReleaseId,
    string                           DatName,
    string                           Title,
    string                           OriginalTitle,
    string                           SortTitle,
    string                           Developer,
    string                           Publisher,
    string                           Year,
    string                           Languages,
    string                           AlternateTitles,
    string                           Description,
    string                           Genre,
    string                           Subgenre,
    string                           Players,
    string                           ReleaseType,
    string                           Rating,
    IReadOnlyList<AmpMediaEntryJson> Media);

internal sealed record AmpMediaEntryJson(
    string  MediaType,
    string  ArchivePath,
    string  FileName,
    string  Sha256,
    long    SizeBytes,
    bool    Preferred,
    string? Credits);

internal sealed record AmpExclusionJson(
    string ReleaseId,
    string DatName,
    string MediaType, // empty string — not tracked per-hash at plan level
    string Sha256);

internal sealed record AmpNoteJson(
    string ReleaseId,
    string DatName,
    string Notes);

internal sealed record AmpFileHashEntry(
    string Path,
    string Sha256,
    long   SizeBytes);
