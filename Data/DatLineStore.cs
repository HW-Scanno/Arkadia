using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Arkadia.Data;

/// <summary>
/// Manages the per-DAT-line SQLite database at data/systems/&lt;platformId&gt;/&lt;datLineId&gt;.db.
/// Owns: releases (and future: pending_reconciliations).
/// </summary>
public sealed class DatLineStore
{
    private readonly string _connectionString;

    public DatLineStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS releases (
                id                   TEXT PRIMARY KEY,
                dat_line_id          TEXT NOT NULL,
                name                 TEXT NOT NULL,
                status               TEXT NOT NULL DEFAULT 'missing',
                tier                 TEXT,
                region               TEXT,
                languages            TEXT,
                format               TEXT,
                size                 TEXT,
                release_content_key  TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_releases_name                ON releases(name);
            CREATE INDEX IF NOT EXISTS idx_releases_release_content_key ON releases(release_content_key);

            CREATE TABLE IF NOT EXISTS release_content_links (
                id                   TEXT PRIMARY KEY,
                release_id           TEXT NOT NULL,
                content_identity_key TEXT NOT NULL,
                created_at_utc       TEXT NOT NULL,
                UNIQUE(release_id, content_identity_key)
            );

            CREATE INDEX IF NOT EXISTS idx_release_content_links_release_id ON release_content_links(release_id);
            CREATE INDEX IF NOT EXISTS idx_release_content_links_cik        ON release_content_links(content_identity_key);

            CREATE TABLE IF NOT EXISTS content_identities (
                content_identity_key TEXT PRIMARY KEY,
                dat_sha1             TEXT,
                dat_md5              TEXT,
                dat_crc32            TEXT,
                created_at_utc       TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS source_artifacts (
                id                   TEXT PRIMARY KEY,
                content_identity_key TEXT NOT NULL,
                source_size_bytes    INTEGER NOT NULL,
                hashed_source_sha1   TEXT NOT NULL DEFAULT '',
                hashed_source_md5    TEXT,
                hashed_source_crc32  TEXT,
                verified_at_utc      TEXT NOT NULL,
                UNIQUE(content_identity_key, hashed_source_sha1, source_size_bytes)
            );

            CREATE INDEX IF NOT EXISTS idx_source_artifacts_cik ON source_artifacts(content_identity_key);

            CREATE TABLE IF NOT EXISTS derived_artifacts (
                id                   TEXT PRIMARY KEY,
                storage_strategy_id  TEXT NOT NULL,
                source_artifact_id   TEXT NOT NULL DEFAULT '',
                content_identity_key TEXT NOT NULL,
                file_name            TEXT NOT NULL,
                relative_path        TEXT NOT NULL,
                derived_size_bytes   INTEGER NOT NULL,
                hashed_derived_sha1  TEXT NOT NULL DEFAULT '',
                hashed_derived_md5   TEXT,
                hashed_derived_crc32 TEXT,
                status               TEXT NOT NULL,
                created_at_utc       TEXT NOT NULL,
                verified_at_utc      TEXT,
                UNIQUE(content_identity_key, storage_strategy_id)
            );

            CREATE INDEX IF NOT EXISTS idx_derived_artifacts_content_key ON derived_artifacts(content_identity_key);

            CREATE TABLE IF NOT EXISTS pending_reconciliations (
                id                   TEXT PRIMARY KEY,
                new_release_id       TEXT NOT NULL,
                outdated_release_id  TEXT NOT NULL,
                artifact_id          TEXT,
                volume_id            TEXT,
                disk_id              TEXT,
                stored_relative_path TEXT,
                stored_name          TEXT,
                target_name          TEXT NOT NULL,
                target_relative_path TEXT,
                reason               TEXT NOT NULL,
                created_at_utc       TEXT NOT NULL,
                status               TEXT NOT NULL DEFAULT 'pending'
            );

            CREATE INDEX IF NOT EXISTS idx_pending_recon_status ON pending_reconciliations(status);

            CREATE TABLE IF NOT EXISTS release_files (
                id          TEXT PRIMARY KEY,
                release_id  TEXT NOT NULL,
                rom_name    TEXT NOT NULL,
                size        TEXT NOT NULL DEFAULT '',
                crc         TEXT NOT NULL DEFAULT '',
                md5         TEXT NOT NULL DEFAULT '',
                sha1        TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_release_files_release_id ON release_files(release_id);
            """;
        cmd.ExecuteNonQuery();

        // ── Migrations for existing databases ─────────────────────────────────
        // Rename content_key → release_content_key on existing releases tables.
        RunMigration(conn, "ALTER TABLE releases RENAME COLUMN content_key TO release_content_key");
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_releases_release_content_key ON releases(release_content_key)");

        // Drop legacy structural tables no longer used in the clean schema.
        RunMigration(conn, "DROP TABLE IF EXISTS artifact_transforms");
        RunMigration(conn, "DROP TABLE IF EXISTS release_artifacts");
        RunMigration(conn, "DROP TABLE IF EXISTS artifacts");

        // Add release_files on databases that predate it.
        RunMigration(conn, """
            CREATE TABLE IF NOT EXISTS release_files (
                id          TEXT PRIMARY KEY,
                release_id  TEXT NOT NULL,
                rom_name    TEXT NOT NULL,
                size        TEXT NOT NULL DEFAULT '',
                crc         TEXT NOT NULL DEFAULT '',
                md5         TEXT NOT NULL DEFAULT '',
                sha1        TEXT NOT NULL DEFAULT ''
            )
            """);
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_release_files_release_id ON release_files(release_id)");

        // Add content_identities / source_artifacts / derived_artifacts for databases that predate them.
        RunMigration(conn, """
            CREATE TABLE IF NOT EXISTS content_identities (
                content_identity_key TEXT PRIMARY KEY,
                dat_sha1             TEXT,
                dat_md5              TEXT,
                dat_crc32            TEXT,
                created_at_utc       TEXT NOT NULL
            )
            """);
        RunMigration(conn, """
            CREATE TABLE IF NOT EXISTS source_artifacts (
                id                   TEXT PRIMARY KEY,
                content_identity_key TEXT NOT NULL,
                source_size_bytes    INTEGER NOT NULL,
                hashed_source_sha1   TEXT NOT NULL DEFAULT '',
                hashed_source_md5    TEXT,
                hashed_source_crc32  TEXT,
                verified_at_utc      TEXT NOT NULL,
                UNIQUE(content_identity_key, hashed_source_sha1, source_size_bytes)
            )
            """);
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_source_artifacts_cik ON source_artifacts(content_identity_key)");
        RunMigration(conn, """
            CREATE TABLE IF NOT EXISTS derived_artifacts (
                id                   TEXT PRIMARY KEY,
                storage_strategy_id  TEXT NOT NULL,
                source_artifact_id   TEXT NOT NULL DEFAULT '',
                content_identity_key TEXT NOT NULL,
                file_name            TEXT NOT NULL,
                relative_path        TEXT NOT NULL,
                derived_size_bytes   INTEGER NOT NULL,
                hashed_derived_sha1  TEXT NOT NULL DEFAULT '',
                hashed_derived_md5   TEXT,
                hashed_derived_crc32 TEXT,
                status               TEXT NOT NULL,
                created_at_utc       TEXT NOT NULL,
                verified_at_utc      TEXT
            )
            """);
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_derived_artifacts_content_key ON derived_artifacts(content_identity_key)");

        // Add release_content_links on databases that predate it.
        RunMigration(conn, """
            CREATE TABLE IF NOT EXISTS release_content_links (
                id                   TEXT PRIMARY KEY,
                release_id           TEXT NOT NULL,
                content_identity_key TEXT NOT NULL,
                created_at_utc       TEXT NOT NULL,
                UNIQUE(release_id, content_identity_key)
            )
            """);
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_release_content_links_release_id ON release_content_links(release_id)");
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_release_content_links_cik        ON release_content_links(content_identity_key)");

        // Add introduced_at_utc marker column for newly-introduced-by-DAT-update tracking.
        RunMigration(conn, "ALTER TABLE releases ADD COLUMN introduced_at_utc TEXT");
    }

    private static void RunMigration(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        try { cmd.ExecuteNonQuery(); } catch { /* already applied */ }
    }

    /// <summary>
    /// Replaces the entire release set for this DAT line.
    /// Deletes all existing rows then inserts the new set in a single transaction.
    /// </summary>
    public void SaveReleases(List<ReleaseRecord> releases)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM releases";
        del.ExecuteNonQuery();

        foreach (var r in releases)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO releases(id, dat_line_id, name, status, tier, region, languages, format, size, release_content_key, introduced_at_utc)
                VALUES($id, $datLineId, $name, $status, $tier, $region, $languages, $format, $size, $contentKey, $introducedAt)
                """;
            cmd.Parameters.AddWithValue("$id",           r.Id);
            cmd.Parameters.AddWithValue("$datLineId",    r.DatLineId);
            cmd.Parameters.AddWithValue("$name",         r.Name);
            cmd.Parameters.AddWithValue("$status",       r.Status);
            cmd.Parameters.AddWithValue("$tier",         r.Tier);
            cmd.Parameters.AddWithValue("$region",       r.Region);
            cmd.Parameters.AddWithValue("$languages",    r.Languages);
            cmd.Parameters.AddWithValue("$format",       r.Format);
            cmd.Parameters.AddWithValue("$size",         r.Size);
            cmd.Parameters.AddWithValue("$contentKey",   r.ReleaseContentKey);
            cmd.Parameters.AddWithValue("$introducedAt", r.IntroducedAtUtc.HasValue
                ? (object)r.IntroducedAtUtc.Value.ToString("o")
                : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        // Remove any pending_reconciliations rows whose new_release_id or
        // outdated_release_id no longer exist in the just-saved release set.
        // This prevents orphaned rows accumulating after each SaveReleases call.
        using var clean = conn.CreateCommand();
        clean.CommandText = """
            DELETE FROM pending_reconciliations
            WHERE new_release_id      NOT IN (SELECT id FROM releases)
               OR outdated_release_id NOT IN (SELECT id FROM releases)
            """;
        clean.ExecuteNonQuery();

        tx.Commit();
    }

    public List<ReleaseRecord> LoadReleases()
    {
        var list = new List<ReleaseRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, dat_line_id, name, status, tier, region, languages, format, size, release_content_key, introduced_at_utc
            FROM releases
            ORDER BY name
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new ReleaseRecord
            {
                Id         = reader.GetString(0),
                DatLineId  = reader.GetString(1),
                Name       = reader.GetString(2),
                Status     = reader.GetString(3),
                Tier       = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Region     = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Languages  = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Format     = reader.IsDBNull(7) ? "" : reader.GetString(7),
                Size       = reader.IsDBNull(8) ? "" : reader.GetString(8),
                ReleaseContentKey  = reader.IsDBNull(9)  ? "" : reader.GetString(9),
                IntroducedAtUtc    = reader.IsDBNull(10) ? null
                    : DateTime.Parse(reader.GetString(10)),
            });
        return list;
    }

    // ── Release Content Links ─────────────────────────────────────────────────

    /// <summary>
    /// Records a structural link between a release and a content-identity key.
    /// Idempotent: silently ignored if the (release_id, content_identity_key) pair already exists.
    /// </summary>
    public void SaveReleaseContentLink(ReleaseContentLinkRecord link)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO release_content_links(id, release_id, content_identity_key, created_at_utc)
            VALUES($id, $releaseId, $cik, $created)
            """;
        cmd.Parameters.AddWithValue("$id",        link.Id);
        cmd.Parameters.AddWithValue("$releaseId", link.ReleaseId);
        cmd.Parameters.AddWithValue("$cik",       link.ContentIdentityKey);
        cmd.Parameters.AddWithValue("$created",   link.CreatedAtUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns all content-identity keys linked to a release.
    /// </summary>
    public HashSet<string> GetContentIdentityKeysByRelease(string releaseId)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT content_identity_key
            FROM release_content_links
            WHERE release_id = $releaseId
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            set.Add(r.GetString(0));
        return set;
    }

    // ── Release Files ─────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces all file entries for a single release.
    /// Deletes existing rows for that release_id then inserts the new set.
    /// </summary>
    public void SaveReleaseFiles(string releaseId, List<ReleaseFileRecord> files)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM release_files WHERE release_id = $rid";
        del.Parameters.AddWithValue("$rid", releaseId);
        del.ExecuteNonQuery();

        foreach (var f in files)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO release_files(id, release_id, rom_name, size, crc, md5, sha1)
                VALUES($id, $releaseId, $romName, $size, $crc, $md5, $sha1)
                """;
            cmd.Parameters.AddWithValue("$id",        f.Id);
            cmd.Parameters.AddWithValue("$releaseId", releaseId);
            cmd.Parameters.AddWithValue("$romName",   f.RomName);
            cmd.Parameters.AddWithValue("$size",      f.Size);
            cmd.Parameters.AddWithValue("$crc",       f.Crc);
            cmd.Parameters.AddWithValue("$md5",       f.Md5);
            cmd.Parameters.AddWithValue("$sha1",      f.Sha1);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Loads all release file entries for this DAT line, grouped by release_id.
    /// </summary>
    public Dictionary<string, List<ReleaseFileRecord>> LoadAllReleaseFiles()
    {
        var result = new Dictionary<string, List<ReleaseFileRecord>>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, release_id, rom_name, size, crc, md5, sha1
            FROM release_files
            ORDER BY release_id, rowid
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var f = new ReleaseFileRecord
            {
                Id        = reader.GetString(0),
                ReleaseId = reader.GetString(1),
                RomName   = reader.GetString(2),
                Size      = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Crc       = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Md5       = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Sha1      = reader.IsDBNull(6) ? "" : reader.GetString(6),
            };
            if (!result.TryGetValue(f.ReleaseId, out var list))
            {
                list = new List<ReleaseFileRecord>();
                result[f.ReleaseId] = list;
            }
            list.Add(f);
        }
        return result;
    }

    // ── Pending Reconciliations ───────────────────────────────────────────────

    /// <summary>
    /// Returns all pending reconciliation rows filtered by <paramref name="status"/>.
    /// Pass null to load all rows.
    /// </summary>
    public List<PendingReconciliationRecord> LoadPendingReconciliations(string? status = "pending")
    {
        var list = new List<PendingReconciliationRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();

        if (status is not null)
        {
            cmd.CommandText = """
                SELECT id, new_release_id, outdated_release_id,
                       artifact_id, volume_id, disk_id,
                       stored_relative_path, stored_name,
                       target_name, target_relative_path,
                       reason, created_at_utc, status
                FROM pending_reconciliations
                WHERE status = $status
                ORDER BY created_at_utc
                """;
            cmd.Parameters.AddWithValue("$status", status);
        }
        else
        {
            cmd.CommandText = """
                SELECT id, new_release_id, outdated_release_id,
                       artifact_id, volume_id, disk_id,
                       stored_relative_path, stored_name,
                       target_name, target_relative_path,
                       reason, created_at_utc, status
                FROM pending_reconciliations
                ORDER BY created_at_utc
                """;
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new PendingReconciliationRecord
            {
                Id                 = reader.GetString(0),
                NewReleaseId       = reader.GetString(1),
                OutdatedReleaseId  = reader.GetString(2),
                ArtifactId         = reader.IsDBNull(3)  ? null : reader.GetString(3),
                VolumeId           = reader.IsDBNull(4)  ? null : reader.GetString(4),
                DiskId             = reader.IsDBNull(5)  ? null : reader.GetString(5),
                StoredRelativePath = reader.IsDBNull(6)  ? null : reader.GetString(6),
                StoredName         = reader.IsDBNull(7)  ? null : reader.GetString(7),
                TargetName         = reader.GetString(8),
                TargetRelativePath = reader.IsDBNull(9)  ? null : reader.GetString(9),
                Reason             = reader.GetString(10),
                CreatedAtUtc       = DateTime.Parse(reader.GetString(11)),
                Status             = reader.GetString(12),
            });
        return list;
    }

    /// <summary>
    /// Inserts a new pending reconciliation row.
    /// </summary>
    public void SavePendingReconciliation(PendingReconciliationRecord r)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pending_reconciliations(
                id, new_release_id, outdated_release_id,
                artifact_id, volume_id, disk_id,
                stored_relative_path, stored_name,
                target_name, target_relative_path,
                reason, created_at_utc, status)
            VALUES(
                $id, $newReleaseId, $outdatedReleaseId,
                $artifactId, $volumeId, $diskId,
                $storedRelativePath, $storedName,
                $targetName, $targetRelativePath,
                $reason, $createdAt, $status)
            """;
        cmd.Parameters.AddWithValue("$id",                 r.Id);
        cmd.Parameters.AddWithValue("$newReleaseId",       r.NewReleaseId);
        cmd.Parameters.AddWithValue("$outdatedReleaseId",  r.OutdatedReleaseId);
        cmd.Parameters.AddWithValue("$artifactId",         (object?)r.ArtifactId         ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$volumeId",           (object?)r.VolumeId           ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$diskId",             (object?)r.DiskId             ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$storedRelativePath", (object?)r.StoredRelativePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$storedName",         (object?)r.StoredName         ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$targetName",         r.TargetName);
        cmd.Parameters.AddWithValue("$targetRelativePath", (object?)r.TargetRelativePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reason",             r.Reason);
        cmd.Parameters.AddWithValue("$createdAt",          r.CreatedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$status",             r.Status);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates the status of a single pending reconciliation row.
    /// </summary>
    public void UpdatePendingReconciliationStatus(string id, string newStatus)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE pending_reconciliations SET status = $status WHERE id = $id";
        cmd.Parameters.AddWithValue("$status", newStatus);
        cmd.Parameters.AddWithValue("$id",     id);
        cmd.ExecuteNonQuery();
    }

    // ── Release-file targeted load ────────────────────────────────────────────

    /// <summary>
    /// Loads all file entries for a single release, ordered by rowid.
    /// Used for completeness checks during ingestion.
    /// </summary>
    public List<ReleaseFileRecord> LoadReleaseFiles(string releaseId)
    {
        var list = new List<ReleaseFileRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, release_id, rom_name, size, crc, md5, sha1
            FROM release_files
            WHERE release_id = $rid
            ORDER BY rowid
            """;
        cmd.Parameters.AddWithValue("$rid", releaseId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new ReleaseFileRecord
            {
                Id        = reader.GetString(0),
                ReleaseId = reader.GetString(1),
                RomName   = reader.GetString(2),
                Size      = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Crc       = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Md5       = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Sha1      = reader.IsDBNull(6) ? "" : reader.GetString(6),
            });
        return list;
    }

    // ── Status update ────────────────────────────────────────────────────────

    public void UpdateReleaseStatus(string releaseId, string status)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE releases SET status = $status WHERE id = $id";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$id",     releaseId);
        cmd.ExecuteNonQuery();
    }

    // ── Status aggregates ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the count of releases with status 'present' and status 'outdated'
    /// without loading every row into memory.
    /// </summary>
    public (int Present, int Outdated) GetStatusCounts()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN status = 'present'  THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'outdated' THEN 1 ELSE 0 END), 0)
            FROM releases
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>
    /// Returns counts for all five release statuses in a single query.
    /// Used by the dashboard to avoid loading every release row into memory.
    /// </summary>
    public (int Missing, int Pending, int Outdated, int Present, int Lost) GetAllStatusCounts()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN status = 'missing'  THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'pending'  THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'outdated' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'present'  THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'lost'     THEN 1 ELSE 0 END), 0)
            FROM releases
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0, 0, 0, 0);
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetInt32(4));
    }

    /// <summary>
    /// Returns other releases in this DAT line that share file content (by SHA1/MD5) with the
    /// given release. Results are sorted by shared-file count DESC, then release name ASC.
    /// </summary>
    public List<(string ReleaseId, string ReleaseName, int SharedCount)> GetHistoricalOverlaps(string releaseId)
    {
        // Load all files once
        var allFiles = LoadAllReleaseFiles();

        if (!allFiles.TryGetValue(releaseId, out var targetFiles) || targetFiles.Count == 0)
            return [];

        // Build identity key → set of release IDs that own a file with that key
        var keyToReleases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relId, files) in allFiles)
        {
            if (relId == releaseId) continue;
            foreach (var f in files)
            {
                var key = !string.IsNullOrEmpty(f.Sha1) ? f.Sha1
                        : !string.IsNullOrEmpty(f.Md5)  ? f.Md5
                        : null;
                if (key is null) continue;
                if (!keyToReleases.TryGetValue(key, out var list))
                    keyToReleases[key] = list = [];
                if (!list.Contains(relId))
                    list.Add(relId);
            }
        }

        // Count shared files per other release
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in targetFiles)
        {
            var key = !string.IsNullOrEmpty(f.Sha1) ? f.Sha1
                    : !string.IsNullOrEmpty(f.Md5)  ? f.Md5
                    : null;
            if (key is null) continue;
            if (!keyToReleases.TryGetValue(key, out var matchedRels)) continue;
            foreach (var relId in matchedRels)
                counts[relId] = counts.GetValueOrDefault(relId) + 1;
        }

        if (counts.Count == 0) return [];

        // Join with release names
        var releases = LoadReleases().ToDictionary(r => r.Id, r => r.Name, StringComparer.Ordinal);

        return counts
            .Where(kv => releases.ContainsKey(kv.Key))
            .Select(kv => (ReleaseId: kv.Key, ReleaseName: releases[kv.Key], SharedCount: kv.Value))
            .OrderByDescending(x => x.SharedCount)
            .ThenBy(x => x.ReleaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Derived Artifacts ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the set of derived_artifact IDs linked to the given release
    /// via release_content_links → derived_artifacts (by content_identity_key).
    /// </summary>
    public HashSet<string> GetDerivedArtifactIdsByRelease(string releaseId)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT da.id
            FROM release_content_links rcl
            JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
            WHERE rcl.release_id = $releaseId
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            set.Add(r.GetString(0));
        return set;
    }

    /// <summary>
    /// Atomically marks the given derived artifacts as lost and marks any
    /// currently-present releases that depend on them as lost.
    /// Uses release_content_links to find affected releases.
    /// Only releases with status "present" are transitioned.
    /// Returns (artifactCount, releaseCount) — count of rows actually updated.
    /// </summary>
    public (int ArtifactCount, int ReleaseCount) MarkArtifactsAndReleasesLost(
        IReadOnlyList<string> derivedIds)
    {
        if (derivedIds.Count == 0) return (0, 0);

        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        var dp = string.Join(",", Enumerable.Range(0, derivedIds.Count).Select(i => $"$d{i}"));

        // Mark derived artifacts lost
        using var artCmd = conn.CreateCommand();
        artCmd.Transaction = tx;
        artCmd.CommandText = $"UPDATE derived_artifacts SET status = 'lost' WHERE id IN ({dp})";
        for (int i = 0; i < derivedIds.Count; i++) artCmd.Parameters.AddWithValue($"$d{i}", derivedIds[i]);
        int artifactCount = artCmd.ExecuteNonQuery();

        // Find releases linked to any of these derived artifacts
        // New chain: derived_artifacts → release_content_links → releases
        var releaseIds = new List<string>();
        using (var findCmd = conn.CreateCommand())
        {
            findCmd.Transaction = tx;
            findCmd.CommandText = $"""
                SELECT DISTINCT rcl.release_id
                FROM release_content_links rcl
                JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
                WHERE da.id IN ({dp})
                """;
            for (int i = 0; i < derivedIds.Count; i++) findCmd.Parameters.AddWithValue($"$d{i}", derivedIds[i]);
            using var rf = findCmd.ExecuteReader();
            while (rf.Read()) releaseIds.Add(rf.GetString(0));
        }

        int releaseCount = 0;
        if (releaseIds.Count > 0)
        {
            var rp = string.Join(",", Enumerable.Range(0, releaseIds.Count).Select(i => $"$r{i}"));
            using var relCmd = conn.CreateCommand();
            relCmd.Transaction = tx;
            // Only "present" releases transition to "lost"; missing/outdated are untouched.
            relCmd.CommandText = $"UPDATE releases SET status = 'lost' WHERE id IN ({rp}) AND status = 'present'";
            for (int i = 0; i < releaseIds.Count; i++) relCmd.Parameters.AddWithValue($"$r{i}", releaseIds[i]);
            releaseCount = relCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return (artifactCount, releaseCount);
    }

    /// <summary>
    /// Sets the status column of the given derived_artifact IDs to <paramref name="status"/>
    /// in a single transaction. Returns the number of rows actually updated.
    /// </summary>
    public int BatchUpdateDerivedArtifactStatus(IReadOnlyList<string> daIds, string status)
    {
        if (daIds.Count == 0) return 0;

        var dp = string.Join(",", Enumerable.Range(0, daIds.Count).Select(i => $"$d{i}"));
        using var conn = Open();
        using var tx   = conn.BeginTransaction();
        using var cmd  = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE derived_artifacts SET status = $status WHERE id IN ({dp})";
        cmd.Parameters.AddWithValue("$status", status);
        for (int i = 0; i < daIds.Count; i++)
            cmd.Parameters.AddWithValue($"$d{i}", daIds[i]);
        int count = cmd.ExecuteNonQuery();
        tx.Commit();
        return count;
    }

    /// <summary>
    /// Recalculates release status for every release linked (via release_content_links)
    /// to any of the given derived artifact IDs.
    /// Rules:
    ///   — "outdated" and "pending" releases are never touched.
    ///   — All other releases: if ALL linked derived artifacts are 'present' → 'present'.
    ///     Otherwise → 'missing'.
    /// Returns the number of release rows actually changed.
    /// </summary>
    public int RecalculateReleaseStatusForArtifacts(IReadOnlyList<string> daIds)
    {
        if (daIds.Count == 0) return 0;

        var dp = string.Join(",", Enumerable.Range(0, daIds.Count).Select(i => $"$d{i}"));
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        // Find all release IDs affected by these derived artifacts.
        var releaseIds = new List<string>();
        using (var findCmd = conn.CreateCommand())
        {
            findCmd.Transaction = tx;
            findCmd.CommandText = $"""
                SELECT DISTINCT rcl.release_id
                FROM release_content_links rcl
                JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
                WHERE da.id IN ({dp})
                """;
            for (int i = 0; i < daIds.Count; i++)
                findCmd.Parameters.AddWithValue($"$d{i}", daIds[i]);
            using var rf = findCmd.ExecuteReader();
            while (rf.Read()) releaseIds.Add(rf.GetString(0));
        }

        if (releaseIds.Count == 0) { tx.Commit(); return 0; }

        // For each affected release determine whether ALL its linked artifacts are 'present'.
        var rp = string.Join(",", Enumerable.Range(0, releaseIds.Count).Select(i => $"$r{i}"));
        int updated = 0;
        using (var updCmd = conn.CreateCommand())
        {
            updCmd.Transaction = tx;
            // Single UPDATE: compute new status inline via correlated sub-selects.
            updCmd.CommandText = $"""
                UPDATE releases
                SET status = CASE
                    WHEN (
                        SELECT COUNT(*)
                        FROM release_content_links rcl
                        JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
                        WHERE rcl.release_id = releases.id
                    ) > 0
                    AND (
                        SELECT COUNT(*)
                        FROM release_content_links rcl
                        JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
                        WHERE rcl.release_id = releases.id AND da.status != 'present'
                    ) = 0
                    THEN 'present'
                    ELSE 'missing'
                END
                WHERE id IN ({rp})
                  AND status NOT IN ('outdated', 'pending')
                """;
            for (int i = 0; i < releaseIds.Count; i++)
                updCmd.Parameters.AddWithValue($"$r{i}", releaseIds[i]);
            updated = updCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return updated;
    }

    public List<DerivedArtifactRecord> GetDerivedArtifacts()
    {
        var list = new List<DerivedArtifactRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, storage_strategy_id, source_artifact_id, content_identity_key,
                   file_name, relative_path, derived_size_bytes,
                   hashed_derived_sha1, hashed_derived_md5, hashed_derived_crc32,
                   status, created_at_utc, verified_at_utc
            FROM derived_artifacts
            ORDER BY file_name
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadDerived(r));
        return list;
    }

    public DerivedArtifactRecord? GetDerivedByContentKey(string contentKey)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, storage_strategy_id, source_artifact_id, content_identity_key,
                   file_name, relative_path, derived_size_bytes,
                   hashed_derived_sha1, hashed_derived_md5, hashed_derived_crc32,
                   status, created_at_utc, verified_at_utc
            FROM derived_artifacts
            WHERE content_identity_key = $ck
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ck", contentKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadDerived(r);
    }

    // ── Content Identities ────────────────────────────────────────────────────

    /// <summary>
    /// Ensures a content_identities row exists for the given key.
    /// Idempotent: INSERT OR IGNORE on the primary key.
    /// </summary>
    public void EnsureContentIdentity(ContentIdentityRecord ci)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO content_identities(content_identity_key, dat_sha1, dat_md5, dat_crc32, created_at_utc)
            VALUES($ck, $sha1, $md5, $crc32, $created)
            """;
        cmd.Parameters.AddWithValue("$ck",      ci.ContentIdentityKey);
        cmd.Parameters.AddWithValue("$sha1",    (object?)ci.DatSha1  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$md5",     (object?)ci.DatMd5   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$crc32",   (object?)ci.DatCrc32 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", ci.CreatedAtUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    // ── Source Artifacts ──────────────────────────────────────────────────────

    /// <summary>
    /// Persists a source provenance record.
    /// Idempotent: INSERT OR IGNORE on (content_identity_key, hashed_source_sha1, source_size_bytes).
    /// </summary>
    public void SaveSourceArtifact(SourceArtifactRecord sa)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO source_artifacts(
                id, content_identity_key, source_size_bytes,
                hashed_source_sha1, hashed_source_md5, hashed_source_crc32, verified_at_utc)
            VALUES($id, $ck, $size, $sha1, $md5, $crc32, $verified)
            """;
        cmd.Parameters.AddWithValue("$id",       sa.Id);
        cmd.Parameters.AddWithValue("$ck",       sa.ContentIdentityKey);
        cmd.Parameters.AddWithValue("$size",     sa.SourceSizeBytes);
        cmd.Parameters.AddWithValue("$sha1",     sa.HashedSourceSha1);
        cmd.Parameters.AddWithValue("$md5",      (object?)sa.HashedSourceMd5   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$crc32",    (object?)sa.HashedSourceCrc32 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$verified", sa.VerifiedAtUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the id of any source_artifact already recorded for a content identity.
    /// Returns null if none exists (should not happen after SaveSourceArtifact succeeds).
    /// </summary>
    public string? GetSourceArtifactIdByContentKey(string contentIdentityKey)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM source_artifacts WHERE content_identity_key = $ck LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ck", contentIdentityKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? r.GetString(0) : null;
    }

    /// <summary>
    /// Returns the first source_artifact for a content identity, or null if none exists.
    /// </summary>
    public SourceArtifactRecord? GetSourceByContentKey(string contentIdentityKey)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, content_identity_key, source_size_bytes,
                   hashed_source_sha1, hashed_source_md5, hashed_source_crc32, verified_at_utc
            FROM source_artifacts
            WHERE content_identity_key = $ck
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ck", contentIdentityKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SourceArtifactRecord
        {
            Id                 = r.GetString(0),
            ContentIdentityKey = r.GetString(1),
            SourceSizeBytes    = r.GetInt64(2),
            HashedSourceSha1   = r.GetString(3),
            HashedSourceMd5    = r.IsDBNull(4) ? null : r.GetString(4),
            HashedSourceCrc32  = r.IsDBNull(5) ? null : r.GetString(5),
            VerifiedAtUtc      = DateTime.Parse(r.GetString(6)),
        };
    }

    // ── Derived Artifacts ─────────────────────────────────────────────────────

    /// <summary>
    /// Upserts a derived artifact by (content_identity_key, storage_strategy_id).
    /// If a row already exists for this pair, updates hashes and status to 'present'.
    /// Returns the derived artifact id (existing or newly inserted).
    /// </summary>
    public string IngestDerivedArtifact(
        string  contentIdentityKey,
        string  sourceArtifactId,
        string  storageStrategyId,
        string  fileName,
        string  relativePath,
        long    derivedSizeBytes,
        string  hashedDerivedSha1,
        string? hashedDerivedMd5   = null,
        string? hashedDerivedCrc32 = null)
    {
        var now       = DateTime.UtcNow;
        var candidateId = Guid.NewGuid().ToString("N");

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO derived_artifacts(
                id, storage_strategy_id, source_artifact_id, content_identity_key,
                file_name, relative_path, derived_size_bytes,
                hashed_derived_sha1, hashed_derived_md5, hashed_derived_crc32,
                status, created_at_utc, verified_at_utc)
            VALUES($id, $stratId, $srcArtId, $ck,
                   $fileName, $relPath, $size,
                   $drvSha1, $drvMd5, $drvCrc32,
                   'present', $created, $created)
            ON CONFLICT(content_identity_key, storage_strategy_id) DO UPDATE SET
                source_artifact_id   = excluded.source_artifact_id,
                hashed_derived_sha1  = excluded.hashed_derived_sha1,
                hashed_derived_md5   = excluded.hashed_derived_md5,
                hashed_derived_crc32 = excluded.hashed_derived_crc32,
                status               = 'present',
                verified_at_utc      = excluded.verified_at_utc
            """;
        cmd.Parameters.AddWithValue("$id",       candidateId);
        cmd.Parameters.AddWithValue("$stratId",  storageStrategyId);
        cmd.Parameters.AddWithValue("$srcArtId", sourceArtifactId);
        cmd.Parameters.AddWithValue("$ck",       contentIdentityKey);
        cmd.Parameters.AddWithValue("$fileName", fileName);
        cmd.Parameters.AddWithValue("$relPath",  relativePath);
        cmd.Parameters.AddWithValue("$size",     derivedSizeBytes);
        cmd.Parameters.AddWithValue("$drvSha1",  hashedDerivedSha1);
        cmd.Parameters.AddWithValue("$drvMd5",   (object?)hashedDerivedMd5   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$drvCrc32", (object?)hashedDerivedCrc32 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created",  now.ToString("o"));
        cmd.ExecuteNonQuery();

        // Retrieve the actual id (may be different from candidateId on conflict).
        using var selCmd = conn.CreateCommand();
        selCmd.CommandText = """
            SELECT id FROM derived_artifacts
            WHERE content_identity_key = $ck AND storage_strategy_id = $stratId
            """;
        selCmd.Parameters.AddWithValue("$ck",      contentIdentityKey);
        selCmd.Parameters.AddWithValue("$stratId", storageStrategyId);
        using var selR = selCmd.ExecuteReader();
        return selR.Read() ? selR.GetString(0) : candidateId;
    }

    // ── Planning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns release-grouped planning candidates for this DAT line.
    /// Uses release_content_links → derived_artifacts (new chain).
    /// </summary>
    public List<PlanningCandidate> GetPlanningCandidates(
        string          appRoot,
        HashSet<string> assignedDerivedIds)
    {
        var releaseToArtifacts = new Dictionary<string, List<DerivedArtifactRecord>>(StringComparer.Ordinal);
        var releaseSeenDaIds   = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var releaseNames       = new Dictionary<string, string>(StringComparer.Ordinal);

        using (var conn = Open())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name FROM releases";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    releaseNames[r.GetString(0)] = r.GetString(1);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT
                        rcl.release_id,
                        da.id, da.storage_strategy_id, da.source_artifact_id, da.content_identity_key,
                        da.file_name, da.relative_path, da.derived_size_bytes,
                        da.hashed_derived_sha1, da.status, da.created_at_utc, da.verified_at_utc
                    FROM release_content_links rcl
                    JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
                    ORDER BY rcl.release_id, da.file_name
                    """;

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var releaseId = r.GetString(0);
                    var daId      = r.GetString(1);

                    if (!releaseSeenDaIds.TryGetValue(releaseId, out var seen))
                    {
                        seen = new HashSet<string>(StringComparer.Ordinal);
                        releaseSeenDaIds[releaseId] = seen;
                    }
                    if (!seen.Add(daId)) continue;

                    var da = new DerivedArtifactRecord
                    {
                        Id                 = daId,
                        StorageStrategyId  = r.GetString(2),
                        SourceArtifactId   = r.GetString(3),
                        ContentIdentityKey = r.GetString(4),
                        FileName           = r.GetString(5),
                        RelativePath       = r.GetString(6),
                        DerivedSizeBytes   = r.GetInt64(7),
                        HashedDerivedSha1  = r.GetString(8),
                        Status             = r.GetString(9),
                        CreatedAtUtc       = DateTime.Parse(r.GetString(10)),
                        VerifiedAtUtc      = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11)),
                    };

                    if (!releaseToArtifacts.TryGetValue(releaseId, out var list))
                    {
                        list = [];
                        releaseToArtifacts[releaseId] = list;
                    }
                    list.Add(da);
                }
            }
        }

        if (releaseToArtifacts.Count == 0)
            return [];

        var candidates = new List<PlanningCandidate>(releaseToArtifacts.Count);

        foreach (var (releaseId, artifacts) in releaseToArtifacts)
        {
            if (!releaseNames.TryGetValue(releaseId, out var releaseName))
                releaseName = releaseId;

            long totalSize   = 0;
            bool anyAssigned = false;
            bool allOnDisk   = artifacts.Count > 0;

            foreach (var da in artifacts)
            {
                totalSize += da.DerivedSizeBytes;

                if (assignedDerivedIds.Contains(da.Id))
                    anyAssigned = true;

                var physicalPath = Path.Combine(
                    appRoot,
                    da.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(physicalPath))
                    allOnDisk = false;
            }

            candidates.Add(new PlanningCandidate
            {
                ReleaseId                    = releaseId,
                ReleaseName                  = releaseName,
                TotalSizeBytes               = totalSize,
                DerivedCount                 = artifacts.Count,
                IsAlreadyAssignedToAnyVolume = anyAssigned,
                IsCompleteInArchive          = allOnDisk,
            });
        }

        return candidates
            .OrderBy(c => c.ReleaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns derived artifact IDs and their content identity keys for the given releases.
    /// Uses release_content_links → derived_artifacts (new chain).
    /// Key = releaseId, Value = list of (DaId, ContentIdentityKey) tuples.
    /// </summary>
    public Dictionary<string, List<(string DaId, string ContentIdentityKey)>>
        GetDerivedArtifactIdsForReleases(IEnumerable<string> releaseIds)
    {
        var result         = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        var seenPerRelease = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        using var conn = Open();
        using var cmd  = conn.CreateCommand();

        var ids = releaseIds as IList<string> ?? releaseIds.ToList();
        if (ids.Count == 0) return result;

        var placeholders = string.Join(",",
            Enumerable.Range(0, ids.Count).Select(i => $"$r{i}"));
        cmd.CommandText = $"""
            SELECT rcl.release_id, da.id, da.content_identity_key
            FROM release_content_links rcl
            JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
            WHERE rcl.release_id IN ({placeholders})
            ORDER BY rcl.release_id, da.id
            """;
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"$r{i}", ids[i]);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var releaseId = r.GetString(0);
            var daId      = r.GetString(1);
            var ck        = r.GetString(2);

            if (!seenPerRelease.TryGetValue(releaseId, out var seen))
            {
                seen = new HashSet<string>(StringComparer.Ordinal);
                seenPerRelease[releaseId] = seen;
            }
            if (!seen.Add(daId)) continue;

            if (!result.TryGetValue(releaseId, out var list))
            {
                list = [];
                result[releaseId] = list;
            }
            list.Add((daId, ck));
        }

        return result;
    }

    /// <summary>
    /// For each derived artifact ID, returns release name, file name, size, and expected SHA1
    /// for volume verification. Uses release_content_links → releases (new chain).
    /// </summary>
    public List<ArtifactVerifyInfo> GetArtifactVerifyInfos(IEnumerable<string> derivedArtifactIds)
    {
        var ids = derivedArtifactIds as IList<string> ?? derivedArtifactIds.ToList();
        if (ids.Count == 0) return [];

        var result   = new List<ArtifactVerifyInfo>(ids.Count);
        var seenDaId = new HashSet<string>(StringComparer.Ordinal);

        var placeholders = string.Join(",",
            Enumerable.Range(0, ids.Count).Select(i => $"$d{i}"));

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT da.id, r.name, da.file_name, da.derived_size_bytes, da.hashed_derived_sha1
            FROM derived_artifacts da
            JOIN release_content_links rcl ON rcl.content_identity_key = da.content_identity_key
            JOIN releases              r   ON r.id = rcl.release_id
            WHERE da.id IN ({placeholders})
            ORDER BY r.name, da.file_name
            """;
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"$d{i}", ids[i]);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var daId = r.GetString(0);
            if (!seenDaId.Add(daId)) continue;
            result.Add(new ArtifactVerifyInfo
            {
                DerivedArtifactId = daId,
                ReleaseName       = r.GetString(1),
                FileName          = r.GetString(2),
                SizeBytes         = r.GetInt64(3),
                Sha1              = r.GetString(4),
            });
        }

        return result;
    }

    /// <summary>
    /// For each derived artifact ID, returns release name, file name, path, and size
    /// for Build Volume. Uses release_content_links → releases (new chain).
    /// </summary>
    public List<ArtifactBuildInfo> GetArtifactBuildInfos(IEnumerable<string> derivedArtifactIds)
    {
        var ids = derivedArtifactIds as IList<string> ?? derivedArtifactIds.ToList();
        if (ids.Count == 0) return [];

        var result   = new List<ArtifactBuildInfo>(ids.Count);
        var seenDaId = new HashSet<string>(StringComparer.Ordinal);

        var placeholders = string.Join(",",
            Enumerable.Range(0, ids.Count).Select(i => $"$d{i}"));

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT da.id, r.name, da.file_name, da.relative_path, da.derived_size_bytes
            FROM derived_artifacts da
            JOIN release_content_links rcl ON rcl.content_identity_key = da.content_identity_key
            JOIN releases              r   ON r.id = rcl.release_id
            WHERE da.id IN ({placeholders})
            ORDER BY r.name, da.file_name
            """;
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"$d{i}", ids[i]);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var daId = r.GetString(0);
            if (!seenDaId.Add(daId)) continue;
            result.Add(new ArtifactBuildInfo
            {
                DerivedArtifactId = daId,
                ReleaseName       = r.GetString(1),
                FileName          = r.GetString(2),
                RelativePath      = r.GetString(3),
                SizeBytes         = r.GetInt64(4),
            });
        }

        return result;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb      = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    // Column order must match SELECT in GetDerivedArtifacts / GetDerivedByContentKey:
    // 0=id, 1=storage_strategy_id, 2=source_artifact_id, 3=content_identity_key,
    // 4=file_name, 5=relative_path, 6=derived_size_bytes,
    // 7=hashed_derived_sha1, 8=hashed_derived_md5, 9=hashed_derived_crc32,
    // 10=status, 11=created_at_utc, 12=verified_at_utc
    private static DerivedArtifactRecord ReadDerived(SqliteDataReader r) => new()
    {
        Id                 = r.GetString(0),
        StorageStrategyId  = r.GetString(1),
        SourceArtifactId   = r.GetString(2),
        ContentIdentityKey = r.GetString(3),
        FileName           = r.GetString(4),
        RelativePath       = r.GetString(5),
        DerivedSizeBytes   = r.GetInt64(6),
        HashedDerivedSha1  = r.GetString(7),
        HashedDerivedMd5   = r.IsDBNull(8)  ? null : r.GetString(8),
        HashedDerivedCrc32 = r.IsDBNull(9)  ? null : r.GetString(9),
        Status             = r.GetString(10),
        CreatedAtUtc       = DateTime.Parse(r.GetString(11)),
        VerifiedAtUtc      = r.IsDBNull(12) ? null : DateTime.Parse(r.GetString(12)),
    };

    // ── Analytics ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns aggregate storage metrics for this DAT line in a single scan.
    /// Collects source totals, derived totals grouped by storage strategy,
    /// and a file-extension count from derived artifact file names.
    /// </summary>
    public DatLineAnalyticsSummary GetAnalyticsSummary()
    {
        using var conn = Open();

        // Total source bytes
        long sourceBytes = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(source_size_bytes), 0) FROM source_artifacts";
            sourceBytes = (long)(cmd.ExecuteScalar() ?? 0L);
        }

        // Derived bytes and count per storage strategy
        var derivedByStrategy = new Dictionary<string, long>(StringComparer.Ordinal);
        long totalDerived = 0;
        int  totalCount   = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT storage_strategy_id,
                       COALESCE(SUM(derived_size_bytes), 0),
                       COUNT(*)
                FROM derived_artifacts
                GROUP BY storage_strategy_id
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var sid   = r.GetString(0);
                var bytes = r.GetInt64(1);
                var cnt   = r.GetInt32(2);
                derivedByStrategy[sid]  = bytes;
                totalDerived           += bytes;
                totalCount             += cnt;
            }
        }

        // File extension distribution from derived artifact file names
        var extCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT file_name FROM derived_artifacts";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var ext = System.IO.Path.GetExtension(r.GetString(0)).ToLowerInvariant();
                if (ext.Length > 0)
                    extCounts[ext] = extCounts.GetValueOrDefault(ext) + 1;
            }
        }

        return new DatLineAnalyticsSummary(
            sourceBytes, totalDerived, derivedByStrategy, extCounts, totalCount);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
