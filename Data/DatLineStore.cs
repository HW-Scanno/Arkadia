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

            CREATE TABLE IF NOT EXISTS artifacts (
                id                   TEXT PRIMARY KEY,
                source_file_name     TEXT NOT NULL,
                source_relative_path TEXT NOT NULL,
                source_size_bytes    INTEGER NOT NULL,
                crc                  TEXT,
                md5                  TEXT,
                sha1                 TEXT,
                content_identity_key TEXT NOT NULL,
                status               TEXT NOT NULL,
                created_at_utc       TEXT NOT NULL,
                verified_at_utc      TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_artifacts_sha1 ON artifacts(sha1);
            CREATE INDEX IF NOT EXISTS idx_artifacts_md5  ON artifacts(md5);

            CREATE TABLE IF NOT EXISTS release_artifacts (
                id             TEXT PRIMARY KEY,
                release_id     TEXT NOT NULL,
                artifact_id    TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_release_artifacts_release_id  ON release_artifacts(release_id);
            CREATE INDEX IF NOT EXISTS idx_release_artifacts_artifact_id ON release_artifacts(artifact_id);

            CREATE TABLE IF NOT EXISTS releases (
                id           TEXT PRIMARY KEY,
                dat_line_id  TEXT NOT NULL,
                name         TEXT NOT NULL,
                status       TEXT NOT NULL DEFAULT 'missing',
                tier         TEXT,
                region       TEXT,
                languages    TEXT,
                format       TEXT,
                size         TEXT,
                content_key  TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_releases_name        ON releases(name);
            CREATE INDEX IF NOT EXISTS idx_releases_content_key ON releases(content_key);

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

        RunMigration(conn, """
            CREATE TABLE IF NOT EXISTS artifacts (
                id                   TEXT PRIMARY KEY,
                source_file_name     TEXT NOT NULL,
                source_relative_path TEXT NOT NULL,
                source_size_bytes    INTEGER NOT NULL,
                crc                  TEXT,
                md5                  TEXT,
                sha1                 TEXT,
                content_identity_key TEXT NOT NULL,
                status               TEXT NOT NULL,
                created_at_utc       TEXT NOT NULL,
                verified_at_utc      TEXT
            )
            """);
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_artifacts_sha1 ON artifacts(sha1)");
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_artifacts_md5  ON artifacts(md5)");
        RunMigration(conn, """
            CREATE TABLE IF NOT EXISTS release_artifacts (
                id             TEXT PRIMARY KEY,
                release_id     TEXT NOT NULL,
                artifact_id    TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            )
            """);
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_release_artifacts_release_id  ON release_artifacts(release_id)");
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_release_artifacts_artifact_id ON release_artifacts(artifact_id)");

        RunMigration(conn, "ALTER TABLE releases ADD COLUMN content_key TEXT NOT NULL DEFAULT ''");
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_releases_content_key ON releases(content_key)");
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
                INSERT INTO releases(id, dat_line_id, name, status, tier, region, languages, format, size, content_key)
                VALUES($id, $datLineId, $name, $status, $tier, $region, $languages, $format, $size, $contentKey)
                """;
            cmd.Parameters.AddWithValue("$id",         r.Id);
            cmd.Parameters.AddWithValue("$datLineId",  r.DatLineId);
            cmd.Parameters.AddWithValue("$name",       r.Name);
            cmd.Parameters.AddWithValue("$status",     r.Status);
            cmd.Parameters.AddWithValue("$tier",       r.Tier);
            cmd.Parameters.AddWithValue("$region",     r.Region);
            cmd.Parameters.AddWithValue("$languages",  r.Languages);
            cmd.Parameters.AddWithValue("$format",     r.Format);
            cmd.Parameters.AddWithValue("$size",       r.Size);
            cmd.Parameters.AddWithValue("$contentKey", r.ContentKey);
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
            SELECT id, dat_line_id, name, status, tier, region, languages, format, size, content_key
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
                ContentKey = reader.IsDBNull(9) ? "" : reader.GetString(9),
            });
        return list;
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

    // ── Artifacts ─────────────────────────────────────────────────────────────

    public void SaveArtifact(ArtifactRecord r)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO artifacts(
                id, source_file_name, source_relative_path, source_size_bytes,
                crc, md5, sha1, content_identity_key,
                status, created_at_utc, verified_at_utc)
            VALUES(
                $id, $sourceFileName, $sourceRelativePath, $sourceSizeBytes,
                $crc, $md5, $sha1, $contentIdentityKey,
                $status, $createdAt, $verifiedAt)
            """;
        cmd.Parameters.AddWithValue("$id",                 r.Id);
        cmd.Parameters.AddWithValue("$sourceFileName",     r.SourceFileName);
        cmd.Parameters.AddWithValue("$sourceRelativePath", r.SourceRelativePath);
        cmd.Parameters.AddWithValue("$sourceSizeBytes",    r.SourceSizeBytes);
        cmd.Parameters.AddWithValue("$crc",                r.Crc.Length  > 0 ? (object)r.Crc  : DBNull.Value);
        cmd.Parameters.AddWithValue("$md5",                r.Md5.Length  > 0 ? (object)r.Md5  : DBNull.Value);
        cmd.Parameters.AddWithValue("$sha1",               r.Sha1.Length > 0 ? (object)r.Sha1 : DBNull.Value);
        cmd.Parameters.AddWithValue("$contentIdentityKey", r.ContentIdentityKey);
        cmd.Parameters.AddWithValue("$status",             r.Status);
        cmd.Parameters.AddWithValue("$createdAt",          r.CreatedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$verifiedAt",         r.VerifiedAtUtc.HasValue
                                                               ? (object)r.VerifiedAtUtc.Value.ToString("o")
                                                               : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void LinkReleaseArtifact(ReleaseArtifactRecord r)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO release_artifacts(id, release_id, artifact_id, created_at_utc)
            VALUES($id, $releaseId, $artifactId, $createdAt)
            """;
        cmd.Parameters.AddWithValue("$id",          r.Id);
        cmd.Parameters.AddWithValue("$releaseId",   r.ReleaseId);
        cmd.Parameters.AddWithValue("$artifactId",  r.ArtifactId);
        cmd.Parameters.AddWithValue("$createdAt",   r.CreatedAtUtc.ToString("o"));
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

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
