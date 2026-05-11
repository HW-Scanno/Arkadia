using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Microsoft.Data.Sqlite;

namespace Arkadia;

public sealed class ArkExportPlanService(string dataDir, CatalogService catalog)
{
    public ArkExportPlan PlanExport(ArkExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stores   = new List<ArkExportPlanStore>();
        var warnings = new List<string>();
        var issues   = new List<string>();

        // ── 1. catalog.db ─────────────────────────────────────────────────────

        var catalogDbPath = Path.Combine(dataDir, "catalog.db");
        if (File.Exists(catalogDbPath))
        {
            var fi = new FileInfo(catalogDbPath);
            stores.Add(new ArkExportPlanStore(
                ArchivePath: "db/catalog.db",
                SourcePath:  catalogDbPath,
                SizeBytes:   fi.Length,
                Category:    "catalog",
                Included:    true));
        }
        else
        {
            issues.Add("catalog.db not found — no catalog database to back up.");
        }

        // ── 2. DAT-line DBs ───────────────────────────────────────────────────

        var datLines = catalog.LoadDatLines();
        foreach (var dl in datLines)
        {
            if (string.IsNullOrEmpty(dl.DataStorePath)) continue;

            var sourcePath  = Path.Combine(dataDir, dl.DataStorePath);
            var archivePath = $"db/systems/{dl.HardwareFamilyId}/{dl.Id}.db";

            if (File.Exists(sourcePath))
            {
                var fi = new FileInfo(sourcePath);
                stores.Add(new ArkExportPlanStore(
                    ArchivePath: archivePath,
                    SourcePath:  sourcePath,
                    SizeBytes:   fi.Length,
                    Category:    "datline",
                    Included:    true));
            }
            else
            {
                warnings.Add($"DAT-line DB not found: {dl.Id} ({sourcePath}).");
            }
        }

        // ── 3. Estimated bytes (DBs) ──────────────────────────────────────────

        long estimatedBytes = stores.Where(s => s.Included).Sum(s => s.SizeBytes);

        // ── 4. Credentials always excluded ────────────────────────────────────

        warnings.Add("Credentials (ScreenScraper keys) are excluded from the backup.");

        // ── 5. Cache packages always excluded ─────────────────────────────────

        warnings.Add("Cache packages are excluded from the backup.");

        // ── 6. Media (optional) ───────────────────────────────────────────────

        long mediaEstimatedBytes = 0;
        if (options.IncludeMedia)
        {
            var mediaRoot = Path.Combine(dataDir, "media");
            if (Directory.Exists(mediaRoot))
            {
                foreach (var f in Directory.EnumerateFiles(mediaRoot, "*", SearchOption.AllDirectories))
                {
                    try { mediaEstimatedBytes += new FileInfo(f).Length; }
                    catch { /* ignore inaccessible files */ }
                }
            }
            estimatedBytes += mediaEstimatedBytes;
            if (mediaEstimatedBytes > 1L * 1024 * 1024 * 1024)
                warnings.Add("Media library exceeds 1 GB — backup will be large.");
        }

        // ── 7. AMP registry (optional) ────────────────────────────────────────

        int ampPackageCount = 0;
        if (options.IncludeAmpRegistry)
        {
            try
            {
                ampPackageCount = new AmpLocalRegistryService(dataDir).ListPackages().Count;
            }
            catch
            {
                warnings.Add("Could not read AMP registry.");
            }
        }

        // ── 8. Scan volume_locations for absolute paths ───────────────────────

        if (File.Exists(catalogDbPath))
        {
            try
            {
                var cs = $"Data Source={catalogDbPath};Mode=ReadOnly";
                using var conn = new SqliteConnection(cs);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*) FROM volume_locations WHERE path IS NOT NULL AND path != ''";
                var count = Convert.ToInt64(cmd.ExecuteScalar());
                if (count > 0)
                    warnings.Add(
                        $"volume_locations contains {count} absolute path(s) — " +
                        "restoring to a different machine may require path remapping.");
            }
            catch
            {
                warnings.Add("Could not scan volume_locations for absolute paths.");
            }
        }

        // ── 9. Scan release_media_curation for absolute paths ─────────────────

        foreach (var s in stores.Where(st => st.Category == "datline"))
        {
            try
            {
                var cs = $"Data Source={s.SourcePath};Mode=ReadOnly";
                using var conn = new SqliteConnection(cs);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*) FROM release_media_curation " +
                    "WHERE file_path IS NOT NULL AND file_path != ''";
                var count = Convert.ToInt64(cmd.ExecuteScalar());
                if (count > 0)
                    warnings.Add(
                        $"release_media_curation ({Path.GetFileNameWithoutExtension(s.SourcePath)}) " +
                        $"contains {count} absolute path(s) — media paths will need remapping after restore.");
            }
            catch
            {
                warnings.Add($"Could not scan release_media_curation in {Path.GetFileName(s.SourcePath)}.");
            }
        }

        return new ArkExportPlan(
            DataDir:                    dataDir,
            Stores:                     stores,
            EstimatedUncompressedBytes: estimatedBytes,
            DatLineCount:               datLines.Count,
            CredentialsExcluded:        true,
            CachePackagesExcluded:      true,
            MediaIncluded:              options.IncludeMedia,
            MediaEstimatedBytes:        mediaEstimatedBytes,
            AmpRegistryIncluded:        options.IncludeAmpRegistry,
            AmpPackageCount:            ampPackageCount,
            Warnings:                   warnings,
            Issues:                     issues);
    }
}
