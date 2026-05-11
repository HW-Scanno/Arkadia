using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Arkadia.Data;
using Microsoft.Data.Sqlite;

namespace Arkadia;

public sealed class ArkWriterService(string dataDir, CatalogService catalog)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ArkWriteResult Write(
        ArkExportOptions  options,
        string            outputPath,
        bool              overwrite = false,
        CancellationToken ct        = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path must not be empty.", nameof(outputPath));

        if (!outputPath.EndsWith(".ark", StringComparison.OrdinalIgnoreCase))
            outputPath += ".ark";

        if (options.IncludeMedia)
            throw new NotSupportedException(
                "Media embedding is not supported in ARK v0.5 Phase 2. Use IncludeMedia = false.");

        if (File.Exists(outputPath) && !overwrite)
            throw new InvalidOperationException(
                $"Output file already exists: '{outputPath}'. Pass overwrite = true to replace.");

        var sidecarPath = outputPath + ".sha256";
        var tmpPath     = outputPath + ".tmp";
        var issues      = new List<string>();
        var tempDbPaths = new List<string>();

        if (options.IncludeSettings)
            issues.Add(
                "Settings export is not implemented in ARK v0.5 Phase 2 and was skipped.");

        try
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);

            var parentDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parentDir))
                Directory.CreateDirectory(parentDir);

            // ── 1. Load DAT lines ────────────────────────────────────────────

            var datLines = catalog.LoadDatLines();

            // ── 2. Backup + sanitize catalog.db ─────────────────────────────

            var catalogDbPath  = Path.Combine(dataDir, "catalog.db");
            string? catalogTemp = null;
            if (File.Exists(catalogDbPath))
            {
                catalogTemp = BackupDatabaseToTemp(catalogDbPath);
                tempDbPaths.Add(catalogTemp);
                SanitizeCatalogDb(catalogTemp);
            }
            else
            {
                issues.Add("catalog.db not found — omitted from backup.");
            }

            // ── 3. Backup + sanitize each DAT-line DB ────────────────────────

            var datStores = new List<(DatLineRecord DatLine, string TempPath)>();
            foreach (var dl in datLines)
            {
                if (string.IsNullOrEmpty(dl.DataStorePath)) continue;

                if (!IsSafePathSegment(dl.HardwareFamilyId) || !IsSafePathSegment(dl.Id))
                {
                    issues.Add($"Skipped DAT line '{dl.Id}': unsafe characters in path segment.");
                    continue;
                }

                var sourcePath = Path.Combine(dataDir, dl.DataStorePath);
                if (!File.Exists(sourcePath))
                {
                    issues.Add(
                        $"DAT-line DB not found: {dl.Id} ({sourcePath}) — omitted from backup.");
                    continue;
                }

                var tempPath = BackupDatabaseToTemp(sourcePath);
                tempDbPaths.Add(tempPath);
                SanitizeDatLineDb(tempPath);
                datStores.Add((dl, tempPath));
            }

            // ── 4. Pre-serialize JSON payloads ───────────────────────────────

            int storeCount    = (catalogTemp != null ? 1 : 0) + datStores.Count;
            var manifestBytes = SerializeManifest(options, datLines.Count, storeCount);

            byte[]? registryBytes = null;
            if (options.IncludeAmpRegistry)
                registryBytes = SerializeRegistry(dataDir);

            // ── 5. Build hash list (excludes hashes/files.sha256.json itself) ─

            var fileHashes = new List<ArkFileHashEntry>();

            fileHashes.Add(new ArkFileHashEntry(
                "manifest.json", HashBytes(manifestBytes), manifestBytes.Length));

            if (registryBytes is not null)
                fileHashes.Add(new ArkFileHashEntry(
                    "registry/amp-packages.json", HashBytes(registryBytes), registryBytes.Length));

            if (catalogTemp is not null)
            {
                var sha  = ReleaseMediaCurationService.ComputeSha256(catalogTemp)
                    ?? throw new InvalidOperationException(
                        "Could not compute SHA-256 of catalog temp DB.");
                fileHashes.Add(new ArkFileHashEntry(
                    "db/catalog.db", sha, new FileInfo(catalogTemp).Length));
            }

            foreach (var (dl, tempPath) in datStores)
            {
                var archivePath = $"db/systems/{dl.HardwareFamilyId}/{dl.Id}.db";
                var sha         = ReleaseMediaCurationService.ComputeSha256(tempPath)
                    ?? throw new InvalidOperationException(
                        $"Could not compute SHA-256 of DAT temp DB: {dl.Id}.");
                fileHashes.Add(new ArkFileHashEntry(
                    archivePath, sha, new FileInfo(tempPath).Length));
            }

            var hashesBytes = JsonSerializer.SerializeToUtf8Bytes(fileHashes, JsonOptions);

            // ── 6. Write ZIP to tmpPath ───────────────────────────────────────

            using (var fs  = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                ct.ThrowIfCancellationRequested();

                WriteEntry(zip, "manifest.json",            manifestBytes);
                WriteEntry(zip, "hashes/files.sha256.json", hashesBytes);

                if (registryBytes is not null)
                    WriteEntry(zip, "registry/amp-packages.json", registryBytes);

                if (catalogTemp is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    WriteDbEntry(zip, "db/catalog.db", catalogTemp);
                }

                foreach (var (dl, tempPath) in datStores)
                {
                    ct.ThrowIfCancellationRequested();
                    WriteDbEntry(zip, $"db/systems/{dl.HardwareFamilyId}/{dl.Id}.db", tempPath);
                }
            }

            // ── 7. Verify, commit, sidecar ───────────────────────────────────

            var tmpInfo = new FileInfo(tmpPath);
            if (!tmpInfo.Exists || tmpInfo.Length == 0)
                throw new InvalidOperationException("Written package is empty or missing.");

            var packageSha = ReleaseMediaCurationService.ComputeSha256(tmpPath)
                ?? throw new InvalidOperationException(
                    "Could not compute SHA-256 of the output package.");
            long packageSize = tmpInfo.Length;

            File.Move(tmpPath, outputPath, overwrite: true);

            File.WriteAllText(
                sidecarPath,
                $"{packageSha}  {Path.GetFileName(outputPath)}{Environment.NewLine}");

            return new ArkWriteResult(
                Success:             true,
                OutputPath:          outputPath,
                SidecarPath:         sidecarPath,
                PackageBytes:        packageSize,
                Sha256:              packageSha,
                DatLineCount:        datLines.Count,
                StoreCount:          storeCount,
                MediaIncluded:       false,
                AmpRegistryIncluded: options.IncludeAmpRegistry,
                Issues:              issues);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
        finally
        {
            foreach (var tp in tempDbPaths)
                try { if (File.Exists(tp)) File.Delete(tp); } catch { }
        }
    }

    // ── SQLite backup ─────────────────────────────────────────────────────────

    private static string BackupDatabaseToTemp(string sourceDbPath)
    {
        var tempPath = Path.GetTempFileName();
        using var src  = new SqliteConnection($"Data Source={sourceDbPath};Pooling=False");
        using var dest = new SqliteConnection($"Data Source={tempPath};Pooling=False");
        src.Open();
        dest.Open();
        src.BackupDatabase(dest);
        return tempPath;
    }

    private static void SanitizeCatalogDb(string tempDbPath)
    {
        using var conn = new SqliteConnection($"Data Source={tempDbPath};Pooling=False");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA foreign_keys = OFF;
                DELETE FROM cache_package_search_terms;
                DELETE FROM cache_package_media;
                DELETE FROM cache_package_games;
                DELETE FROM cache_packages;
                DELETE FROM settings
                WHERE key IN (
                    'screenscraper_username',
                    'screenscraper_password',
                    'screenscraper_dev_id',
                    'screenscraper_dev_password',
                    'screenscraper_softname'
                );
                PRAGMA foreign_keys = ON;
                """;
            cmd.ExecuteNonQuery();
        }

        using (var vacuumCmd = conn.CreateCommand())
        {
            vacuumCmd.CommandText = "VACUUM";
            vacuumCmd.ExecuteNonQuery();
        }
    }

    private static void SanitizeDatLineDb(string tempDbPath)
    {
        using var conn = new SqliteConnection($"Data Source={tempDbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM release_provider_payloads;
            DELETE FROM pending_reconciliations;
            """;
        cmd.ExecuteNonQuery();
    }

    // ── JSON serialization ────────────────────────────────────────────────────

    private static byte[] SerializeManifest(ArkExportOptions options, int datLineCount, int storeCount)
    {
        var obj = new ArkManifestJson(
            FormatName:            "Arkadia Backup",
            FormatVersion:         "0.5",
            CreatedAtUtc:          DateTime.UtcNow.ToString("O"),
            ArkadiaAppVersion:     null,
            CredentialsExcluded:   true,
            CachePackagesExcluded: true,
            MediaIncluded:         false,
            AmpRegistryIncluded:   options.IncludeAmpRegistry,
            DatLineCount:          datLineCount,
            StoreCount:            storeCount,
            HashAlgorithm:         "SHA-256");
        return JsonSerializer.SerializeToUtf8Bytes(obj, JsonOptions);
    }

    private static byte[] SerializeRegistry(string dataDir)
    {
        var packages = new AmpLocalRegistryService(dataDir).ListPackages();
        var entries  = packages
            .Select(p => new ArkAmpRegistryEntryJson(
                FileName:         p.FileName,
                RegistryPath:     $"scrape-cache/arkadia-media-packs/{p.FileName}",
                PackageSha256:    p.PackageSha256,
                Status:           p.Status,
                HardwareFamilyId: p.HardwareFamilyId,
                DatLineId:        p.DatLineId,
                SystemName:       p.SystemName,
                ReleaseCount:     p.ReleaseCount,
                MediaFileCount:   p.MediaFileCount,
                TotalMediaBytes:  p.TotalMediaBytes,
                LastWriteTimeUtc: p.LastWriteTimeUtc.ToString("O")))
            .ToList();
        return JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
    }

    // ── ZIP helpers ───────────────────────────────────────────────────────────

    private static void WriteEntry(ZipArchive zip, string entryPath, byte[] content)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private static void WriteDbEntry(ZipArchive zip, string entryPath, string tempFilePath)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var fileStream  = File.OpenRead(tempFilePath);
        fileStream.CopyTo(entryStream);
    }

    // ── Path safety ───────────────────────────────────────────────────────────

    private static bool IsSafePathSegment(string s) =>
        s.Length > 0
        && !s.Contains('/')
        && !s.Contains('\\')
        && s != "..";

    // ── Hashing ───────────────────────────────────────────────────────────────

    private static string HashBytes(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }
}

// ── Internal JSON models ──────────────────────────────────────────────────────

internal sealed record ArkManifestJson(
    string  FormatName,
    string  FormatVersion,
    string  CreatedAtUtc,
    string? ArkadiaAppVersion,
    bool    CredentialsExcluded,
    bool    CachePackagesExcluded,
    bool    MediaIncluded,
    bool    AmpRegistryIncluded,
    int     DatLineCount,
    int     StoreCount,
    string  HashAlgorithm);

internal sealed record ArkAmpRegistryEntryJson(
    string FileName,
    string RegistryPath,
    string PackageSha256,
    string Status,
    string HardwareFamilyId,
    string DatLineId,
    string SystemName,
    int    ReleaseCount,
    int    MediaFileCount,
    long   TotalMediaBytes,
    string LastWriteTimeUtc);

internal sealed record ArkFileHashEntry(
    string Path,
    string Sha256,
    long   SizeBytes);
