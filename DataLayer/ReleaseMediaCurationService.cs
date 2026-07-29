using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Arkadia.Data;

// ── Public record ──────────────────────────────────────────────────────────────

public sealed record ReleaseMediaAsset(
    string  ReleaseId,
    string  MediaType,
    string  FilePath,
    string  FileName,
    long    SizeBytes,
    string? Sha256,
    bool    Exists,
    bool    IsPreferred,
    bool    IsExcluded,
    string? ExcludedReason,
    string? Credits,
    string? Notes)
{
    public string StatusLabel => IsExcluded ? "Excluded" :
                                 !Exists    ? "Missing"  :
                                 IsPreferred ? "Preferred" : "Active";

    public string SizeDisplay => SizeBytes switch
    {
        0                   => "—",
        < 1024              => $"{SizeBytes} B",
        < 1024 * 1024       => $"{SizeBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024):F1} MB",
        _                   => $"{SizeBytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}

// ── Import result ─────────────────────────────────────────────────────────────

public sealed record MediaImportResult(bool Success, string? ErrorMessage, ReleaseMediaAsset? Asset);

// ── Service ───────────────────────────────────────────────────────────────────

public sealed class ReleaseMediaCurationService(string dataDir)
{
    // Folder names under data/media/<hwFamilyId>/<datLineId>/ mapped to canonical media type IDs.
    // "metadata" folder is intentionally excluded — internal use only.
    private static readonly Dictionary<string, string> FolderToMediaType = new()
    {
        ["covers-front"]      = "cover-front",
        ["covers-back"]       = "cover-back",
        ["covers-spine"]      = "cover-spine",
        ["covers-wrap"]       = "cover-wrap",
        ["screenshots-title"] = "screenshot-title",
        ["screenshots"]       = "screenshot",
        ["fanart"]            = "fanart",
        ["videos"]            = "video",
        ["logos-hd"]          = "logo-hd",
        ["logos"]             = "logo",
        ["manuals"]           = "manual",
        ["marquees"]          = "marquee",
        ["flyers"]            = "flyer",
        ["physical"]          = "physical",
        ["physical-texture"]  = "physical-texture",
    };

    private static readonly Dictionary<string, string> MediaTypeToFolder =
        FolderToMediaType.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    // Canonical display order for the UI
    internal static readonly string[] MediaTypeOrder =
    [
        "cover-front", "cover-back", "cover-spine", "cover-wrap",
        "screenshot-title", "screenshot",
        "video",
        "logo-hd", "logo",
        "fanart",
        "physical", "physical-texture",
        "manual",
        "marquee", "flyer",
    ];

    // ── Asset discovery ───────────────────────────────────────────────────────

    public IReadOnlyList<ReleaseMediaAsset> LoadAssets(
        string dbPath, string releaseId, string releaseName,
        string hardwareFamilyId, string datLineId)
    {
        var store       = new DatLineStore(dbPath);
        var curationRows = store.LoadMediaCurationRows(releaseId)
                                .ToDictionary(r => r.FilePath, StringComparer.OrdinalIgnoreCase);

        var mediaRoot = MediaStore.DatLinePath(dataDir, hardwareFamilyId, datLineId);
        var discovered = DiscoverFiles(mediaRoot, releaseName);

        var result          = new List<ReleaseMediaAsset>();
        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, mediaType) in discovered)
        {
            discoveredPaths.Add(filePath);
            curationRows.TryGetValue(filePath, out var row);
            var size = new FileInfo(filePath).Length;
            result.Add(new ReleaseMediaAsset(
                ReleaseId:      releaseId,
                MediaType:      mediaType,
                FilePath:       filePath,
                FileName:       Path.GetFileName(filePath),
                SizeBytes:      size,
                Sha256:         row?.FileSha256,
                Exists:         true,
                IsPreferred:    row?.IsPreferred   ?? false,
                IsExcluded:     row?.IsExcluded    ?? false,
                ExcludedReason: row?.ExcludedReason,
                Credits:        row?.Credits,
                Notes:          row?.Notes));
        }

        // Include curation rows whose file is missing so excluded/deleted assets remain visible.
        foreach (var row in curationRows.Values)
        {
            if (!discoveredPaths.Contains(row.FilePath))
            {
                result.Add(new ReleaseMediaAsset(
                    ReleaseId:      releaseId,
                    MediaType:      MediaStore.NormalizeMediaType(row.MediaType),
                    FilePath:       row.FilePath,
                    FileName:       Path.GetFileName(row.FilePath),
                    SizeBytes:      0,
                    Sha256:         row.FileSha256,
                    Exists:         false,
                    IsPreferred:    row.IsPreferred,
                    IsExcluded:     row.IsExcluded,
                    ExcludedReason: row.ExcludedReason,
                    Credits:        row.Credits,
                    Notes:          row.Notes));
            }
        }

        return result
            .OrderBy(a => MediaTypeRank(a.MediaType))
            .ThenBy(a => a.FileName)
            .ToList();
    }

    // ── Curation operations ───────────────────────────────────────────────────

    public void SetPreferred(string dbPath, string releaseId, string mediaType, string filePath)
    {
        mediaType = MediaStore.NormalizeMediaType(mediaType);
        // Atomic: clears previous preferred and marks the new one in a single transaction.
        new DatLineStore(dbPath).SetPreferredMediaCuration(releaseId, mediaType, filePath);
    }

    public void Exclude(string dbPath, string releaseId, string mediaType, string filePath, string? reason)
    {
        mediaType = MediaStore.NormalizeMediaType(mediaType);
        var sha256   = File.Exists(filePath) ? ComputeSha256(filePath) : null;
        var store    = new DatLineStore(dbPath);
        var existing = store.LoadMediaCurationRows(releaseId)
                            .FirstOrDefault(r => string.Equals(r.FilePath, filePath,
                                                StringComparison.OrdinalIgnoreCase));
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      releaseId,
            MediaType:      mediaType,
            FilePath:       filePath,
            FileSha256:     sha256 ?? existing?.FileSha256,
            IsPreferred:    false,
            IsExcluded:     true,
            ExcludedReason: reason,
            Credits:        existing?.Credits,
            Notes:          existing?.Notes));
    }

    public void Restore(string dbPath, string releaseId, string mediaType, string filePath)
    {
        var store    = new DatLineStore(dbPath);
        var existing = store.LoadMediaCurationRows(releaseId)
                            .FirstOrDefault(r => string.Equals(r.FilePath, filePath,
                                                StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        store.UpsertMediaCurationRow(existing with
        {
            MediaType      = MediaStore.NormalizeMediaType(existing.MediaType),
            IsExcluded     = false,
            ExcludedReason = null,
        });
    }

    public void SaveCredits(string dbPath, string releaseId, string mediaType, string filePath, string? credits)
    {
        mediaType = MediaStore.NormalizeMediaType(mediaType);
        var store    = new DatLineStore(dbPath);
        var existing = store.LoadMediaCurationRows(releaseId)
                            .FirstOrDefault(r => string.Equals(r.FilePath, filePath,
                                                StringComparison.OrdinalIgnoreCase));
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      releaseId,
            MediaType:      mediaType,
            FilePath:       filePath,
            FileSha256:     existing?.FileSha256,
            IsPreferred:    existing?.IsPreferred    ?? false,
            IsExcluded:     existing?.IsExcluded     ?? false,
            ExcludedReason: existing?.ExcludedReason,
            Credits:        string.IsNullOrWhiteSpace(credits) ? null : credits.Trim(),
            Notes:          existing?.Notes));
    }

    // Delete File is local cleanup only: removes the file from disk and its curation row from the
    // DB. It does NOT create an exclusion — the asset may be reintroduced by a future scrape or
    // import. Use Exclude() instead when the intent is to permanently reject an asset.
    public void DeleteMediaFile(string dbPath, string releaseId, string mediaType, string filePath)
    {
        mediaType = MediaStore.NormalizeMediaType(mediaType);
        GuardMediaPath(filePath);

        // File deletion happens first. If it throws the DB row is untouched.
        if (File.Exists(filePath))
            File.Delete(filePath);

        // Row removal happens only after the file is gone (or was already absent).
        new DatLineStore(dbPath).DeleteMediaCurationRow(releaseId, mediaType, filePath);
    }

    // ── Add media ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the destination path where a new media file should be copied.
    /// For cover types the canonical region "wor" is used by default.
    /// </summary>
    public string GetNextMediaDestPath(
        string hardwareFamilyId, string datLineId, string releaseName,
        string mediaType, string extension, string coverRegion = "wor")
    {
        mediaType = MediaStore.NormalizeMediaType(mediaType);
        if (!MediaTypeToFolder.TryGetValue(mediaType, out var folder))
            throw new ArgumentException($"Unknown media type: {mediaType}");

        var isCover = mediaType.StartsWith("cover-", StringComparison.Ordinal);
        return isCover
            ? MediaStore.NextIndexedCoverStem(dataDir, hardwareFamilyId, datLineId,
                  releaseName, folder, coverRegion)
              + (extension.StartsWith('.') ? extension : "." + extension)
            : MediaStore.NextIndexedMediaPath(dataDir, hardwareFamilyId, datLineId,
                  releaseName, folder, extension);
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to the correct media folder,
    /// registers a curation row, and returns the resulting <see cref="ReleaseMediaAsset"/>.
    /// </summary>
    public ReleaseMediaAsset AddMediaFile(
        string dbPath, string releaseId, string releaseName,
        string hardwareFamilyId, string datLineId,
        string sourcePath, string mediaType, string coverRegion = "wor")
    {
        mediaType = MediaStore.NormalizeMediaType(mediaType);
        var ext  = Path.GetExtension(sourcePath);
        var dest = GetNextMediaDestPath(hardwareFamilyId, datLineId, releaseName, mediaType, ext, coverRegion);

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(sourcePath, dest, overwrite: false);

        // File was freshly copied; clean it up if the curation write fails.
        try
        {
            var sha256 = ComputeSha256(dest);
            var store  = new DatLineStore(dbPath);

            // Mark preferred only when no rows exist yet for this type — excluded rows
            // count, so re-uploading after a previous exclude does not auto-set preferred.
            var existing = store.LoadMediaCurationRows(releaseId)
                                .Where(r => r.MediaType == mediaType)
                                .ToList();
            var setPreferred = existing.Count == 0;
            if (setPreferred) store.ClearPreferredForType(releaseId, mediaType);

            var row = new MediaCurationRow(
                ReleaseId:      releaseId,
                MediaType:      mediaType,
                FilePath:       dest,
                FileSha256:     sha256,
                IsPreferred:    setPreferred,
                IsExcluded:     false,
                ExcludedReason: null,
                Credits:        null,
                Notes:          null);
            store.UpsertMediaCurationRow(row);

            return new ReleaseMediaAsset(
                ReleaseId:      releaseId,
                MediaType:      mediaType,
                FilePath:       dest,
                FileName:       Path.GetFileName(dest),
                SizeBytes:      new FileInfo(dest).Length,
                Sha256:         sha256,
                Exists:         true,
                IsPreferred:    setPreferred,
                IsExcluded:     false,
                ExcludedReason: null,
                Credits:        null,
                Notes:          null);
        }
        catch
        {
            // Best-effort: remove the newly copied file so no orphan is left on disk.
            try { File.Delete(dest); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Copies a file from an incoming folder into the release's media folder with
    /// SHA-256 transfer verification, then creates a curation row.
    /// The curation row is never created until destination hash matches source hash.
    /// </summary>
    public MediaImportResult ImportFromIncoming(
        string dbPath, string releaseId, string releaseName,
        string hardwareFamilyId, string datLineId,
        string sourcePath, string mediaType,
        bool deleteSourceAfterSuccess = false)
    {
        if (!File.Exists(sourcePath) || Directory.Exists(sourcePath))
            return new(false, "Source file not found.", null);

        mediaType = MediaStore.NormalizeMediaType(mediaType);
        if (!MediaTypeToFolder.ContainsKey(mediaType))
            return new(false, $"Unknown media type: {mediaType}", null);

        var sourceHash = ComputeSha256(sourcePath);
        if (sourceHash is null)
            return new(false, "Could not read source file.", null);

        var ext  = Path.GetExtension(sourcePath);
        var dest = GetNextMediaDestPath(hardwareFamilyId, datLineId, releaseName, mediaType, ext);

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest),
                StringComparison.OrdinalIgnoreCase))
            return new(false, "Source and destination are the same file.", null);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(sourcePath, dest, overwrite: false);
        }
        catch (Exception ex)
        {
            return new(false, $"Copy failed: {ex.Message}", null);
        }

        var destHash = ComputeSha256(dest);
        if (destHash is null || !string.Equals(sourceHash, destHash, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(dest); } catch { }
            return new(false, "Hash mismatch after copy. File may be corrupt.", null);
        }

        try
        {
            var store    = new DatLineStore(dbPath);
            var existing = store.LoadMediaCurationRows(releaseId)
                                .Where(r => r.MediaType == mediaType)
                                .ToList();
            var setPreferred = existing.Count == 0;
            if (setPreferred) store.ClearPreferredForType(releaseId, mediaType);

            var row = new MediaCurationRow(
                ReleaseId:      releaseId,
                MediaType:      mediaType,
                FilePath:       dest,
                FileSha256:     destHash,
                IsPreferred:    setPreferred,
                IsExcluded:     false,
                ExcludedReason: null,
                Credits:        null,
                Notes:          null);
            store.UpsertMediaCurationRow(row);

            if (deleteSourceAfterSuccess)
                try { File.Delete(sourcePath); } catch { }

            var asset = new ReleaseMediaAsset(
                ReleaseId:      releaseId,
                MediaType:      mediaType,
                FilePath:       dest,
                FileName:       Path.GetFileName(dest),
                SizeBytes:      new FileInfo(dest).Length,
                Sha256:         destHash,
                Exists:         true,
                IsPreferred:    setPreferred,
                IsExcluded:     false,
                ExcludedReason: null,
                Credits:        null,
                Notes:          null);

            return new(true, null, asset);
        }
        catch (Exception ex)
        {
            try { File.Delete(dest); } catch { }
            return new(false, $"Failed to save curation record: {ex.Message}", null);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<(string FilePath, string MediaType)> DiscoverFiles(string mediaRoot, string releaseName)
    {
        var result = new List<(string, string)>();
        var stem   = MediaStore.ReleaseStem(releaseName) + "_";

        foreach (var (folder, mediaType) in FolderToMediaType)
        {
            var dir = Path.Combine(mediaRoot, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, stem + "*").OrderBy(f => f))
                result.Add((file, mediaType));
        }

        return result;
    }

    private void GuardMediaPath(string filePath)
    {
        var mediaRoot = Path.GetFullPath(Path.Combine(dataDir, "media"));
        var full      = Path.GetFullPath(filePath);

        if (!full.StartsWith(mediaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "File path is outside the Arkadia media root. Deletion refused.", nameof(filePath));

        if (Directory.Exists(full))
            throw new ArgumentException("Path is a directory, not a file. Deletion refused.", nameof(filePath));
    }

    private static int MediaTypeRank(string mediaType)
    {
        var idx = Array.IndexOf(MediaTypeOrder, mediaType);
        return idx < 0 ? MediaTypeOrder.Length : idx;
    }

    public static string? ComputeSha256(string filePath)
    {
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }
        catch { return null; }
    }
}
