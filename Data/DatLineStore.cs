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

        RunMigration(conn, """
            CREATE TABLE IF NOT EXISTS derived_artifacts (
                id                   TEXT PRIMARY KEY,
                storage_strategy_id  TEXT NOT NULL,
                file_name            TEXT NOT NULL,
                relative_path        TEXT NOT NULL,
                size_bytes           INTEGER NOT NULL,
                crc                  TEXT,
                md5                  TEXT,
                sha1                 TEXT,
                content_identity_key TEXT NOT NULL,
                status               TEXT NOT NULL,
                created_at_utc       TEXT NOT NULL,
                verified_at_utc      TEXT
            )
            """);
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_derived_artifacts_content_key ON derived_artifacts(content_identity_key)");

        RunMigration(conn, """
            CREATE TABLE IF NOT EXISTS artifact_transforms (
                id                  TEXT PRIMARY KEY,
                source_artifact_id  TEXT NOT NULL,
                derived_artifact_id TEXT NOT NULL,
                transform_kind      TEXT NOT NULL,
                created_at_utc      TEXT NOT NULL
            )
            """);
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_artifact_transforms_source  ON artifact_transforms(source_artifact_id)");
        RunMigration(conn, "CREATE INDEX IF NOT EXISTS idx_artifact_transforms_derived ON artifact_transforms(derived_artifact_id)");
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

    // ── Derived Artifacts ─────────────────────────────────────────────────────

    public List<DerivedArtifactRecord> GetDerivedArtifacts()
    {
        var list = new List<DerivedArtifactRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, storage_strategy_id, file_name, relative_path, size_bytes,
                   crc, md5, sha1, content_identity_key, status, created_at_utc, verified_at_utc
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
            SELECT id, storage_strategy_id, file_name, relative_path, size_bytes,
                   crc, md5, sha1, content_identity_key, status, created_at_utc, verified_at_utc
            FROM derived_artifacts
            WHERE content_identity_key = $ck
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ck", contentKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadDerived(r);
    }

    public void SaveDerivedArtifact(DerivedArtifactRecord d)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO derived_artifacts(
                id, storage_strategy_id, file_name, relative_path, size_bytes,
                crc, md5, sha1, content_identity_key, status, created_at_utc, verified_at_utc)
            VALUES(
                $id, $stratId, $fileName, $relPath, $size,
                $crc, $md5, $sha1, $ck, $status, $created, $verified)
            ON CONFLICT(id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$id",       d.Id);
        cmd.Parameters.AddWithValue("$stratId",  d.StorageStrategyId);
        cmd.Parameters.AddWithValue("$fileName", d.FileName);
        cmd.Parameters.AddWithValue("$relPath",  d.RelativePath);
        cmd.Parameters.AddWithValue("$size",     d.SizeBytes);
        cmd.Parameters.AddWithValue("$crc",      NullIfEmpty(d.Crc));
        cmd.Parameters.AddWithValue("$md5",      NullIfEmpty(d.Md5));
        cmd.Parameters.AddWithValue("$sha1",     NullIfEmpty(d.Sha1));
        cmd.Parameters.AddWithValue("$ck",       d.ContentIdentityKey);
        cmd.Parameters.AddWithValue("$status",   d.Status);
        cmd.Parameters.AddWithValue("$created",  d.CreatedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$verified", d.VerifiedAtUtc.HasValue
            ? d.VerifiedAtUtc.Value.ToString("o") : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void SaveArtifactTransform(string id, string sourceArtifactId,
        string derivedArtifactId, string transformKind, DateTime createdAtUtc)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO artifact_transforms(id, source_artifact_id, derived_artifact_id, transform_kind, created_at_utc)
            VALUES($id, $srcId, $drvId, $kind, $created)
            ON CONFLICT(id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$id",      id);
        cmd.Parameters.AddWithValue("$srcId",   sourceArtifactId);
        cmd.Parameters.AddWithValue("$drvId",   derivedArtifactId);
        cmd.Parameters.AddWithValue("$kind",    transformKind);
        cmd.Parameters.AddWithValue("$created", createdAtUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public bool ArtifactTransformExists(string sourceArtifactId, string derivedArtifactId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM artifact_transforms
            WHERE source_artifact_id = $srcId AND derived_artifact_id = $drvId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$srcId", sourceArtifactId);
        cmd.Parameters.AddWithValue("$drvId", derivedArtifactId);
        using var r = cmd.ExecuteReader();
        return r.Read();
    }

    /// <summary>
    /// No-compression transform: copies the source artifact file to archive/ as-is,
    /// records a DerivedArtifactRecord, and links it via artifact_transforms.
    /// Idempotent: reuses existing derived artifact if content_identity_key already present.
    /// Returns the derived artifact id, or null on failure.
    /// </summary>
    public string? RunNoCompressionTransform(
        string         sourceArtifactId,
        string         sourceFilePath,
        string         fileName,
        long           sizeBytes,
        string         crc,
        string         md5,
        string         sha1,
        string         contentIdentityKey,
        string         platformId,
        string         datLineId,
        string         releaseFolderName,
        string         storageStrategyId,
        string         appRoot)
    {
        var now = DateTime.UtcNow;

        // ── Idempotency: check if derived artifact already exists ─────────────
        var existing = GetDerivedByContentKey(contentIdentityKey);
        string derivedId;

        if (existing is null)
        {
            var archiveDir = Path.Combine(appRoot, "archive", platformId, datLineId, releaseFolderName);
            Directory.CreateDirectory(archiveDir);
            var destPath   = Path.Combine(archiveDir, fileName);
            var relPath    = $"archive/{platformId}/{datLineId}/{releaseFolderName}/{fileName}";

            // Copy (or verify if already on disk)
            if (File.Exists(destPath))
            {
                long existingSize = 0;
                try { existingSize = new FileInfo(destPath).Length; } catch { }
                if (existingSize != sizeBytes)
                    File.Copy(sourceFilePath, destPath, overwrite: true);
                // else: correct file already there — reuse
            }
            else
            {
                File.Copy(sourceFilePath, destPath, overwrite: true);
            }

            derivedId = Guid.NewGuid().ToString("N");
            SaveDerivedArtifact(new DerivedArtifactRecord
            {
                Id                 = derivedId,
                StorageStrategyId  = storageStrategyId,
                FileName           = fileName,
                RelativePath       = relPath,
                SizeBytes          = sizeBytes,
                Crc                = crc,
                Md5                = md5,
                Sha1               = sha1,
                ContentIdentityKey = contentIdentityKey,
                Status             = "present",
                CreatedAtUtc       = now,
                VerifiedAtUtc      = now,
            });
        }
        else
        {
            derivedId = existing.Id;
        }

        // ── Provenance: insert transform link if not already recorded ─────────
        if (!ArtifactTransformExists(sourceArtifactId, derivedId))
        {
            SaveArtifactTransform(
                Guid.NewGuid().ToString("N"),
                sourceArtifactId,
                derivedId,
                "no_compression",
                now);
        }

        return derivedId;
    }

    // ── Planning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns release-grouped planning candidates for this DAT line.
    /// Each candidate represents one complete release group with its derived artifacts.
    /// </summary>
    /// <param name="appRoot">
    ///   Application base directory — used to resolve archive paths for completeness check.
    /// </param>
    /// <param name="assignedDerivedIds">
    ///   Set of derived_artifact_id values already present in volume_artifacts (any volume).
    ///   Build this from <c>CatalogService</c> before calling.
    /// </param>
    public List<PlanningCandidate> GetPlanningCandidates(
        string              appRoot,
        HashSet<string>     assignedDerivedIds)
    {
        // ── 1. Load all derived artifacts per release via the join chain ──────
        // releases → release_artifacts → artifact_transforms → derived_artifacts
        // A derived artifact can appear more than once per release when multiple
        // source artifacts share the same content_identity_key (idempotent transform
        // reuses the same derived_artifact_id). Track seen IDs per release to avoid
        // double-counting size and inflating DerivedCount / IsCompleteInArchive.
        var releaseToArtifacts = new Dictionary<string, List<DerivedArtifactRecord>>(
            StringComparer.Ordinal);
        // Per-release dedup set: releaseId → set of da.id already added
        var releaseSeenDaIds = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal);
        var releaseNames = new Dictionary<string, string>(StringComparer.Ordinal);

        using (var conn = Open())
        {
            // Collect release names first
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name FROM releases";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    releaseNames[r.GetString(0)] = r.GetString(1);
            }

            // Single query: walk the full chain
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT
                        ra.release_id,
                        da.id, da.storage_strategy_id, da.file_name, da.relative_path,
                        da.size_bytes, da.crc, da.md5, da.sha1,
                        da.content_identity_key, da.status, da.created_at_utc, da.verified_at_utc
                    FROM release_artifacts   ra
                    JOIN artifact_transforms at ON at.source_artifact_id  = ra.artifact_id
                    JOIN derived_artifacts   da ON da.id                  = at.derived_artifact_id
                    ORDER BY ra.release_id, da.file_name
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
                    // Skip duplicate derived artifact within the same release.
                    if (!seen.Add(daId))
                        continue;

                    var da = new DerivedArtifactRecord
                    {
                        Id                 = daId,
                        StorageStrategyId  = r.GetString(2),
                        FileName           = r.GetString(3),
                        RelativePath       = r.GetString(4),
                        SizeBytes          = r.GetInt64(5),
                        Crc                = r.IsDBNull(6)  ? "" : r.GetString(6),
                        Md5                = r.IsDBNull(7)  ? "" : r.GetString(7),
                        Sha1               = r.IsDBNull(8)  ? "" : r.GetString(8),
                        ContentIdentityKey = r.GetString(9),
                        Status             = r.GetString(10),
                        CreatedAtUtc       = DateTime.Parse(r.GetString(11)),
                        VerifiedAtUtc      = r.IsDBNull(12) ? null : DateTime.Parse(r.GetString(12)),
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

        // ── 2. Build candidates ───────────────────────────────────────────────
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
                totalSize += da.SizeBytes;

                if (assignedDerivedIds.Contains(da.Id))
                    anyAssigned = true;

                // Resolve physical path from relative_path (uses forward slashes).
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
    /// Returns distinct derived_artifact_id values for each of the given release IDs,
    /// using the same join chain as GetPlanningCandidates.
    /// Key = releaseId, Value = distinct derived artifact IDs for that release.
    /// Only releases that have at least one derived artifact appear in the result.
    /// </summary>
    public Dictionary<string, List<string>> GetDerivedArtifactIdsForReleases(
        IEnumerable<string> releaseIds)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // Per-release dedup (same idempotent-reuse concern as GetPlanningCandidates).
        var seenPerRelease = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        using var conn = Open();
        using var cmd  = conn.CreateCommand();

        // Build a parameterised IN list.
        var ids = releaseIds as IList<string> ?? releaseIds.ToList();
        if (ids.Count == 0) return result;

        var placeholders = string.Join(",",
            System.Linq.Enumerable.Range(0, ids.Count).Select(i => $"$r{i}"));
        cmd.CommandText = $"""
            SELECT ra.release_id, da.id
            FROM release_artifacts   ra
            JOIN artifact_transforms at ON at.source_artifact_id  = ra.artifact_id
            JOIN derived_artifacts   da ON da.id                  = at.derived_artifact_id
            WHERE ra.release_id IN ({placeholders})
            ORDER BY ra.release_id, da.id
            """;
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"$r{i}", ids[i]);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var releaseId = r.GetString(0);
            var daId      = r.GetString(1);

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
            list.Add(daId);
        }

        return result;
    }

    /// <summary>
    /// For each given derived_artifact_id, returns the release name, file name,
    /// archive relative path, and size. Used by the Build Volume handler to resolve
    /// physical source paths and construct destination folder layout.
    /// If a derived artifact is linked to multiple releases (edge case), the first
    /// encountered release name is used.
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
            SELECT da.id, r.name, da.file_name, da.relative_path, da.size_bytes
            FROM derived_artifacts   da
            JOIN artifact_transforms at ON at.derived_artifact_id = da.id
            JOIN release_artifacts   ra ON ra.artifact_id         = at.source_artifact_id
            JOIN releases            r  ON r.id                   = ra.release_id
            WHERE da.id IN ({placeholders})
            ORDER BY r.name, da.file_name
            """;
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"$d{i}", ids[i]);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var daId = r.GetString(0);
            if (!seenDaId.Add(daId)) continue;   // keep first (lowest release name) only
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

    private static object NullIfEmpty(string s) => s.Length > 0 ? s : DBNull.Value;

    private static DerivedArtifactRecord ReadDerived(SqliteDataReader r) => new()
    {
        Id                 = r.GetString(0),
        StorageStrategyId  = r.GetString(1),
        FileName           = r.GetString(2),
        RelativePath       = r.GetString(3),
        SizeBytes          = r.GetInt64(4),
        Crc                = r.IsDBNull(5)  ? "" : r.GetString(5),
        Md5                = r.IsDBNull(6)  ? "" : r.GetString(6),
        Sha1               = r.IsDBNull(7)  ? "" : r.GetString(7),
        ContentIdentityKey = r.GetString(8),
        Status             = r.GetString(9),
        CreatedAtUtc       = DateTime.Parse(r.GetString(10)),
        VerifiedAtUtc      = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11)),
    };

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
