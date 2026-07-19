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
                release_content_key  TEXT NOT NULL DEFAULT '',
                introduced_at_utc    TEXT,
                content_category_id  TEXT NOT NULL DEFAULT 'games'
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
                archive_tier         TEXT NOT NULL DEFAULT 'B',
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

            CREATE TABLE IF NOT EXISTS release_metadata (
                release_id       TEXT PRIMARY KEY,
                title            TEXT NOT NULL DEFAULT '',
                original_title   TEXT NOT NULL DEFAULT '',
                developer        TEXT NOT NULL DEFAULT '',
                publisher        TEXT NOT NULL DEFAULT '',
                year             TEXT NOT NULL DEFAULT '',
                languages        TEXT NOT NULL DEFAULT '',
                alternate_titles TEXT NOT NULL DEFAULT '',
                description      TEXT NOT NULL DEFAULT '',
                scraped_at_utc   TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS release_provider_payloads (
                release_id  TEXT NOT NULL,
                provider    TEXT NOT NULL,
                payload     TEXT NOT NULL DEFAULT '{}',
                scraped_at  TEXT NOT NULL,
                PRIMARY KEY (release_id, provider)
            );

            CREATE TABLE IF NOT EXISTS release_metadata_field_state (
                release_id      TEXT NOT NULL,
                field           TEXT NOT NULL,
                source          TEXT NOT NULL DEFAULT '',
                provider        TEXT NOT NULL DEFAULT '',
                locked          INTEGER NOT NULL DEFAULT 0,
                updated_at_utc  TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (release_id, field)
            );

            CREATE TABLE IF NOT EXISTS release_metadata_proposals (
                release_id   TEXT NOT NULL,
                provider     TEXT NOT NULL,
                field        TEXT NOT NULL,
                value        TEXT NOT NULL DEFAULT '',
                scraped_at   TEXT NOT NULL,
                accepted     INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (release_id, provider, field)
            );

            CREATE TABLE IF NOT EXISTS release_media_curation (
                id              INTEGER PRIMARY KEY,
                release_id      TEXT NOT NULL,
                media_type      TEXT NOT NULL,
                file_path       TEXT NOT NULL,
                file_sha256     TEXT,
                is_preferred    INTEGER NOT NULL DEFAULT 0,
                is_excluded     INTEGER NOT NULL DEFAULT 0,
                excluded_reason TEXT,
                credits         TEXT,
                notes           TEXT,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                UNIQUE(release_id, media_type, file_path)
            );

            CREATE INDEX IF NOT EXISTS idx_rmc_release_type ON release_media_curation(release_id, media_type);
            CREATE INDEX IF NOT EXISTS idx_rmc_sha256 ON release_media_curation(file_sha256);

            CREATE TABLE IF NOT EXISTS release_extra_notes (
                release_id TEXT PRIMARY KEY,
                notes      TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Normalize "physical-media" rows to canonical "physical".
        // Rows with a conflicting (release_id, 'physical', file_path) are deleted first
        // to avoid UNIQUE constraint violations during the rename.
        using (var mig = Open())
        {
            using var del = mig.CreateCommand();
            del.CommandText = """
                DELETE FROM release_media_curation
                WHERE media_type = 'physical-media'
                  AND EXISTS (
                      SELECT 1 FROM release_media_curation r2
                      WHERE r2.release_id = release_media_curation.release_id
                        AND r2.file_path  = release_media_curation.file_path
                        AND r2.media_type = 'physical'
                  )
                """;
            del.ExecuteNonQuery();

            using var upd = mig.CreateCommand();
            upd.CommandText = """
                UPDATE release_media_curation
                SET media_type = 'physical', updated_at = strftime('%Y-%m-%dT%H:%M:%SZ','now')
                WHERE media_type = 'physical-media'
                """;
            upd.ExecuteNonQuery();
        }

        // Add columns introduced in v2 to release_metadata.
        // TryAddColumn is idempotent — ignored if the column already exists.
        using var alter = Open();
        TryAddColumn(alter, "release_metadata", "sort_title    TEXT NOT NULL DEFAULT ''");
        TryAddColumn(alter, "release_metadata", "genre         TEXT NOT NULL DEFAULT ''");
        TryAddColumn(alter, "release_metadata", "subgenre      TEXT NOT NULL DEFAULT ''");
        TryAddColumn(alter, "release_metadata", "players       TEXT NOT NULL DEFAULT ''");
        TryAddColumn(alter, "release_metadata", "release_type  TEXT NOT NULL DEFAULT ''");
        TryAddColumn(alter, "release_metadata", "rating        TEXT NOT NULL DEFAULT ''");
        TryAddColumn(alter, "release_metadata", "notes         TEXT NOT NULL DEFAULT ''");

        // Add show_in_catalog (v3 migration). Existing rows default to 1 (visible).
        TryAddColumn(alter, "releases", "show_in_catalog INTEGER NOT NULL DEFAULT 1");
    }

    private static void TryAddColumn(SqliteConnection conn, string table, string columnDef)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {columnDef}";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* column already exists — safe to ignore */ }
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
                INSERT INTO releases(id, dat_line_id, name, status, tier, region, languages, format, size, release_content_key, introduced_at_utc, content_category_id, show_in_catalog)
                VALUES($id, $datLineId, $name, $status, $tier, $region, $languages, $format, $size, $contentKey, $introducedAt, $contentCategoryId, $showInCatalog)
                """;
            cmd.Parameters.AddWithValue("$id",                r.Id);
            cmd.Parameters.AddWithValue("$datLineId",         r.DatLineId);
            cmd.Parameters.AddWithValue("$name",              r.Name);
            cmd.Parameters.AddWithValue("$status",            r.Status);
            cmd.Parameters.AddWithValue("$tier",              r.Tier);
            cmd.Parameters.AddWithValue("$region",            r.Region);
            cmd.Parameters.AddWithValue("$languages",         r.Languages);
            cmd.Parameters.AddWithValue("$format",            r.Format);
            cmd.Parameters.AddWithValue("$size",              r.Size);
            cmd.Parameters.AddWithValue("$contentKey",        r.ReleaseContentKey);
            cmd.Parameters.AddWithValue("$introducedAt",      r.IntroducedAtUtc.HasValue
                ? (object)r.IntroducedAtUtc.Value.ToString("o")
                : DBNull.Value);
            cmd.Parameters.AddWithValue("$contentCategoryId", r.ContentCategoryId.Length > 0 ? r.ContentCategoryId : "games");
            cmd.Parameters.AddWithValue("$showInCatalog",     r.ShowInCatalog ? 1 : 0);
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

    /// <summary>
    /// Inserts or updates a single release row without touching any other rows.
    /// Useful for test helpers and incremental provisioning.
    /// </summary>
    public void UpsertRelease(ReleaseRecord r)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO releases(id, dat_line_id, name, status, tier, region, languages, format, size,
                                 release_content_key, introduced_at_utc, content_category_id, show_in_catalog)
            VALUES($id, $datLineId, $name, $status, $tier, $region, $languages, $format, $size,
                   $contentKey, $introducedAt, $contentCategoryId, $showInCatalog)
            ON CONFLICT(id) DO UPDATE SET
                name                = excluded.name,
                status              = excluded.status,
                tier                = excluded.tier,
                region              = excluded.region,
                languages           = excluded.languages,
                format              = excluded.format,
                size                = excluded.size,
                release_content_key = excluded.release_content_key,
                introduced_at_utc   = excluded.introduced_at_utc,
                content_category_id = excluded.content_category_id,
                show_in_catalog     = excluded.show_in_catalog
            """;
        cmd.Parameters.AddWithValue("$id",               r.Id);
        cmd.Parameters.AddWithValue("$datLineId",         r.DatLineId);
        cmd.Parameters.AddWithValue("$name",              r.Name);
        cmd.Parameters.AddWithValue("$status",            r.Status);
        cmd.Parameters.AddWithValue("$tier",              r.Tier);
        cmd.Parameters.AddWithValue("$region",            r.Region);
        cmd.Parameters.AddWithValue("$languages",         r.Languages);
        cmd.Parameters.AddWithValue("$format",            r.Format);
        cmd.Parameters.AddWithValue("$size",              r.Size);
        cmd.Parameters.AddWithValue("$contentKey",        r.ReleaseContentKey);
        cmd.Parameters.AddWithValue("$introducedAt",      r.IntroducedAtUtc.HasValue
            ? (object)r.IntroducedAtUtc.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$contentCategoryId", r.ContentCategoryId.Length > 0 ? r.ContentCategoryId : "games");
        cmd.Parameters.AddWithValue("$showInCatalog",     r.ShowInCatalog ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public List<ReleaseRecord> LoadReleases()
    {
        var list = new List<ReleaseRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.dat_line_id, r.name, r.status,
                   COALESCE(
                       NULLIF(r.tier, ''),
                       (SELECT da.archive_tier
                        FROM release_content_links rcl
                        JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
                        WHERE rcl.release_id = r.id
                        LIMIT 1),
                       ''
                   ) AS effective_tier,
                   r.region, r.languages, r.format, r.size, r.release_content_key, r.introduced_at_utc,
                   r.content_category_id, COALESCE(r.show_in_catalog, 1)
            FROM releases r
            ORDER BY r.name
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
                ReleaseContentKey   = reader.IsDBNull(9)  ? "" : reader.GetString(9),
                IntroducedAtUtc     = reader.IsDBNull(10) ? null
                    : DateTime.Parse(reader.GetString(10)),
                ContentCategoryId   = reader.IsDBNull(11) ? "games" : reader.GetString(11),
                ShowInCatalog       = reader.IsDBNull(12) || reader.GetInt64(12) != 0,
            });
        return list;
    }

    public List<ReleaseRecord> LoadReleasesByDatLine(string datLineId)
    {
        var list = new List<ReleaseRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.dat_line_id, r.name, r.status,
                   COALESCE(
                       NULLIF(r.tier, ''),
                       (SELECT da.archive_tier
                        FROM release_content_links rcl
                        JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
                        WHERE rcl.release_id = r.id
                        LIMIT 1),
                       ''
                   ) AS effective_tier,
                   r.region, r.languages, r.format, r.size, r.release_content_key, r.introduced_at_utc,
                   r.content_category_id, COALESCE(r.show_in_catalog, 1)
            FROM releases r
            WHERE r.dat_line_id = $datLineId
            ORDER BY r.name
            """;
        cmd.Parameters.AddWithValue("$datLineId", datLineId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new ReleaseRecord
            {
                Id                = reader.GetString(0),
                DatLineId         = reader.GetString(1),
                Name              = reader.GetString(2),
                Status            = reader.GetString(3),
                Tier              = reader.IsDBNull(4)  ? "" : reader.GetString(4),
                Region            = reader.IsDBNull(5)  ? "" : reader.GetString(5),
                Languages         = reader.IsDBNull(6)  ? "" : reader.GetString(6),
                Format            = reader.IsDBNull(7)  ? "" : reader.GetString(7),
                Size              = reader.IsDBNull(8)  ? "" : reader.GetString(8),
                ReleaseContentKey = reader.IsDBNull(9)  ? "" : reader.GetString(9),
                IntroducedAtUtc   = reader.IsDBNull(10) ? null
                    : DateTime.Parse(reader.GetString(10)),
                ContentCategoryId = reader.IsDBNull(11) ? "games" : reader.GetString(11),
                ShowInCatalog     = reader.IsDBNull(12) || reader.GetInt64(12) != 0,
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

    // ── Release metadata ─────────────────────────────────────────────────────

    public Dictionary<string, ReleaseMetadataRecord> LoadReleaseMetadata()
    {
        var dict = new Dictionary<string, ReleaseMetadataRecord>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT release_id, title, original_title, developer, publisher, year, languages,
                   alternate_titles, description, scraped_at_utc,
                   sort_title, genre, subgenre, players, release_type, rating, notes
            FROM release_metadata
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            dict[id] = new ReleaseMetadataRecord
            {
                ReleaseId       = id,
                Title           = reader.IsDBNull(1)  ? "" : reader.GetString(1),
                OriginalTitle   = reader.IsDBNull(2)  ? "" : reader.GetString(2),
                Developer       = reader.IsDBNull(3)  ? "" : reader.GetString(3),
                Publisher       = reader.IsDBNull(4)  ? "" : reader.GetString(4),
                Year            = reader.IsDBNull(5)  ? "" : reader.GetString(5),
                Languages       = reader.IsDBNull(6)  ? "" : reader.GetString(6),
                AlternateTitles = reader.IsDBNull(7)  ? "" : reader.GetString(7),
                Description     = reader.IsDBNull(8)  ? "" : reader.GetString(8),
                ScrapedAtUtc    = reader.IsDBNull(9)  ? "" : reader.GetString(9),
                SortTitle       = reader.IsDBNull(10) ? "" : reader.GetString(10),
                Genre           = reader.IsDBNull(11) ? "" : reader.GetString(11),
                Subgenre        = reader.IsDBNull(12) ? "" : reader.GetString(12),
                Players         = reader.IsDBNull(13) ? "" : reader.GetString(13),
                ReleaseType     = reader.IsDBNull(14) ? "" : reader.GetString(14),
                Rating          = reader.IsDBNull(15) ? "" : reader.GetString(15),
                Notes           = reader.IsDBNull(16) ? "" : reader.GetString(16),
            };
        }
        return dict;
    }

    /// <summary>
    /// Upserts a metadata record for the given release.
    /// Inserts a new row or fully replaces an existing one.
    /// </summary>
    public void SaveReleaseMetadata(ReleaseMetadataRecord m)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO release_metadata(
                release_id, title, original_title, developer, publisher,
                year, languages, alternate_titles, description, scraped_at_utc,
                sort_title, genre, subgenre, players, release_type, rating, notes)
            VALUES(
                $id, $title, $origTitle, $dev, $pub,
                $year, $langs, $alts, $desc, $scraped,
                $sortTitle, $genre, $subgenre, $players, $releaseType, $rating, $notes)
            ON CONFLICT(release_id) DO UPDATE SET
                title            = excluded.title,
                original_title   = excluded.original_title,
                developer        = excluded.developer,
                publisher        = excluded.publisher,
                year             = excluded.year,
                languages        = excluded.languages,
                alternate_titles = excluded.alternate_titles,
                description      = excluded.description,
                scraped_at_utc   = excluded.scraped_at_utc,
                sort_title       = excluded.sort_title,
                genre            = excluded.genre,
                subgenre         = excluded.subgenre,
                players          = excluded.players,
                release_type     = excluded.release_type,
                rating           = excluded.rating,
                notes            = excluded.notes
            """;
        cmd.Parameters.AddWithValue("$id",          m.ReleaseId);
        cmd.Parameters.AddWithValue("$title",       m.Title);
        cmd.Parameters.AddWithValue("$origTitle",   m.OriginalTitle);
        cmd.Parameters.AddWithValue("$dev",         m.Developer);
        cmd.Parameters.AddWithValue("$pub",         m.Publisher);
        cmd.Parameters.AddWithValue("$year",        m.Year);
        cmd.Parameters.AddWithValue("$langs",       m.Languages);
        cmd.Parameters.AddWithValue("$alts",        m.AlternateTitles);
        cmd.Parameters.AddWithValue("$desc",        m.Description);
        cmd.Parameters.AddWithValue("$scraped",     m.ScrapedAtUtc);
        cmd.Parameters.AddWithValue("$sortTitle",   m.SortTitle);
        cmd.Parameters.AddWithValue("$genre",       m.Genre);
        cmd.Parameters.AddWithValue("$subgenre",    m.Subgenre);
        cmd.Parameters.AddWithValue("$players",     m.Players);
        cmd.Parameters.AddWithValue("$releaseType", m.ReleaseType);
        cmd.Parameters.AddWithValue("$rating",      m.Rating);
        cmd.Parameters.AddWithValue("$notes",       m.Notes);
        cmd.ExecuteNonQuery();
    }

    // ── Provider payloads ────────────────────────────────────────────────────

    /// <summary>
    /// Upserts the raw provider payload for a release.
    /// Updates both payload and scraped_at when the (release_id, provider) pair already exists.
    /// </summary>
    public void SaveProviderPayload(string releaseId, string provider, string payloadJson)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO release_provider_payloads(release_id, provider, payload, scraped_at)
            VALUES($id, $provider, $payload, $scraped)
            ON CONFLICT(release_id, provider) DO UPDATE SET
                payload    = excluded.payload,
                scraped_at = excluded.scraped_at
            """;
        cmd.Parameters.AddWithValue("$id",       releaseId);
        cmd.Parameters.AddWithValue("$provider", provider);
        cmd.Parameters.AddWithValue("$payload",  payloadJson);
        cmd.Parameters.AddWithValue("$scraped",  DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the stored payload JSON for the given (release_id, provider) pair,
    /// or null if no row exists.
    /// </summary>
    public string? LoadProviderPayload(string releaseId, string provider)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT payload FROM release_provider_payloads
            WHERE release_id = $id AND provider = $provider
            """;
        cmd.Parameters.AddWithValue("$id",       releaseId);
        cmd.Parameters.AddWithValue("$provider", provider);
        return cmd.ExecuteScalar() as string;
    }

    // ── Release field updates ─────────────────────────────────────────────────

    /// <summary>Persists a user-edited region value back to the canonical releases row.</summary>
    public void UpdateReleaseRegion(string releaseId, string region)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE releases SET region = $region WHERE id = $id";
        cmd.Parameters.AddWithValue("$region", region);
        cmd.Parameters.AddWithValue("$id",     releaseId);
        cmd.ExecuteNonQuery();
    }

    // ── Status update ────────────────────────────────────────────────────────

    public void UpdateReleaseStatus(string releaseId, string status)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        // Generic lifecycle updates never touch unwanted releases.
        // Only RestoreWantedRelease may intentionally leave the unwanted state.
        cmd.CommandText = "UPDATE releases SET status = $status WHERE id = $id AND status != 'unwanted'";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$id",     releaseId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The only allowed way to remove a release from the unwanted state.
    /// Sets status → missing and show_in_catalog → 1.
    /// Must be called by the explicit "Restore Wanted" UI action only.
    /// </summary>
    public void RestoreWantedRelease(string releaseId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE releases
            SET status = 'missing', show_in_catalog = 1
            WHERE id = $id AND status = 'unwanted'
            """;
        cmd.Parameters.AddWithValue("$id", releaseId);
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
    /// Returns counts for all release statuses in a single query.
    /// Used by the dashboard and analytics to avoid loading every release row.
    /// </summary>
    public (int Missing, int Pending, int Outdated, int Present, int Lost, int Unwanted) GetAllStatusCounts()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN status = 'missing'  THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'pending'  THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'outdated' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'present'  THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'lost'     THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'unwanted' THEN 1 ELSE 0 END), 0)
            FROM releases
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0, 0, 0, 0, 0);
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
    }

    /// <summary>
    /// Returns the count and total derived size bytes of derived artifacts with
    /// status 'present' that are not present in <paramref name="assignedIds"/>.
    /// Used by the Systems detail pane to show how much unallocated material remains.
    /// </summary>
    public (int Count, long TotalBytes) GetUnassignedPresentStats(
        System.Collections.Generic.IReadOnlySet<string> assignedIds)
    {
        int  count = 0;
        long total = 0;
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, derived_size_bytes FROM derived_artifacts WHERE status = 'present'";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!assignedIds.Contains(r.GetString(0)))
            {
                count++;
                total += r.GetInt64(1);
            }
        }
        return (count, total);
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
                    WHEN (
                        SELECT COUNT(*)
                        FROM release_content_links rcl
                        JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
                        WHERE rcl.release_id = releases.id AND da.status = 'lost'
                    ) > 0
                    AND (
                        SELECT COUNT(*)
                        FROM release_content_links rcl
                        JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
                        WHERE rcl.release_id = releases.id AND da.status = 'present'
                    ) = 0
                    THEN 'lost'
                    ELSE 'missing'
                END
                WHERE id IN ({rp})
                  AND status NOT IN ('outdated', 'pending', 'unwanted')
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
                   status, created_at_utc, verified_at_utc, archive_tier
            FROM derived_artifacts
            ORDER BY file_name
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadDerived(r));
        return list;
    }

    /// <summary>
    /// Returns the distinct content_identity_keys of derived artifacts recorded at the
    /// given <paramref name="relativePath"/>. Used by the archive write collision guard:
    /// an empty result with a physically-present target means the target is unclaimed;
    /// a key different from the one being written means a genuine collision.
    /// </summary>
    public List<string> GetDerivedArtifactContentKeysByRelativePath(string relativePath)
    {
        var list = new List<string>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT content_identity_key
            FROM derived_artifacts
            WHERE relative_path = $rp
            """;
        cmd.Parameters.AddWithValue("$rp", relativePath);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(r.GetString(0));
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
                   status, created_at_utc, verified_at_utc, archive_tier
            FROM derived_artifacts
            WHERE content_identity_key = $ck
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ck", contentKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadDerived(r);
    }

    public List<DerivedArtifactRecord> GetDerivedArtifactsByReleaseId(string releaseId)
    {
        var list = new List<DerivedArtifactRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT da.id, da.storage_strategy_id, da.source_artifact_id, da.content_identity_key,
                   da.file_name, da.relative_path, da.derived_size_bytes,
                   da.hashed_derived_sha1, da.hashed_derived_md5, da.hashed_derived_crc32,
                   da.status, da.created_at_utc, da.verified_at_utc, da.archive_tier
            FROM release_content_links rcl
            JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
            WHERE rcl.release_id = $releaseId
            ORDER BY da.file_name
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadDerived(r));
        return list;
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
    /// Returns source SHA1s that could satisfy the given content identity keys.
    /// Combines hashed_source_sha1 from source_artifacts and dat_sha1 from content_identities.
    /// Used by Repair Volumes Pass B to identify incoming source files eligible for Tier A rebuild.
    /// </summary>
    public HashSet<string> GetSourceSha1sForContentKeys(IEnumerable<string> contentKeys)
    {
        var keys = contentKeys as IList<string> ?? contentKeys.ToList();
        if (keys.Count == 0) return [];

        var result       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var placeholders = string.Join(",", Enumerable.Range(0, keys.Count).Select(i => $"$k{i}"));
        using var conn   = Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT hashed_source_sha1 FROM source_artifacts
                WHERE content_identity_key IN ({placeholders}) AND hashed_source_sha1 != ''
                """;
            for (int i = 0; i < keys.Count; i++) cmd.Parameters.AddWithValue($"$k{i}", keys[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT dat_sha1 FROM content_identities
                WHERE content_identity_key IN ({placeholders})
                  AND dat_sha1 IS NOT NULL AND dat_sha1 != ''
                """;
            for (int i = 0; i < keys.Count; i++) cmd.Parameters.AddWithValue($"$k{i}", keys[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
        }

        return result;
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
        string? hashedDerivedCrc32 = null,
        string  archiveTier        = "B")
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
                status, created_at_utc, verified_at_utc, archive_tier)
            VALUES($id, $stratId, $srcArtId, $ck,
                   $fileName, $relPath, $size,
                   $drvSha1, $drvMd5, $drvCrc32,
                   'present', $created, $created, $tier)
            ON CONFLICT(content_identity_key, storage_strategy_id) DO UPDATE SET
                source_artifact_id   = excluded.source_artifact_id,
                hashed_derived_sha1  = excluded.hashed_derived_sha1,
                hashed_derived_md5   = excluded.hashed_derived_md5,
                hashed_derived_crc32 = excluded.hashed_derived_crc32,
                status               = 'present',
                verified_at_utc      = excluded.verified_at_utc,
                archive_tier         = excluded.archive_tier
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
        cmd.Parameters.AddWithValue("$tier",     archiveTier.Length > 0 ? archiveTier : "B");
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
                // Only load wanted releases; unwanted are excluded from Build Volume.
                cmd.CommandText = "SELECT id, name FROM releases WHERE status != 'unwanted'";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    releaseNames[r.GetString(0)] = r.GetString(1);
            }

            using (var cmd = conn.CreateCommand())
            {
                // Exclude artifacts linked to ANY unwanted release (UNWANTED WINS semantics).
                cmd.CommandText = """
                    SELECT
                        rcl.release_id,
                        da.id, da.storage_strategy_id, da.source_artifact_id, da.content_identity_key,
                        da.file_name, da.relative_path, da.derived_size_bytes,
                        da.hashed_derived_sha1, da.status, da.created_at_utc, da.verified_at_utc,
                        da.archive_tier
                    FROM release_content_links rcl
                    JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
                    JOIN releases r ON r.id = rcl.release_id
                    WHERE r.status != 'unwanted'
                      AND NOT EXISTS (
                        SELECT 1 FROM release_content_links rcl2
                        JOIN releases r2 ON r2.id = rcl2.release_id
                        WHERE rcl2.content_identity_key = da.content_identity_key
                          AND r2.status = 'unwanted'
                      )
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
                        ArchiveTier        = r.IsDBNull(12) ? "B"  : r.GetString(12),
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
            SELECT da.id, r.name, da.file_name, da.derived_size_bytes, da.hashed_derived_sha1, da.relative_path
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
                RelativePath      = r.IsDBNull(5) ? "" : r.GetString(5),
            });
        }

        return result;
    }

    /// <summary>
    /// Like <see cref="GetArtifactVerifyInfos"/> but excludes artifacts whose linked
    /// release has status 'unwanted'. Used by the Fillback planner to build the
    /// candidate list without including unwanted content.
    /// </summary>
    public List<ArtifactVerifyInfo> GetFillbackCandidateInfos(IReadOnlyList<string> derivedArtifactIds)
    {
        if (derivedArtifactIds.Count == 0) return [];

        var result   = new List<ArtifactVerifyInfo>(derivedArtifactIds.Count);
        var seenDaId = new HashSet<string>(StringComparer.Ordinal);

        var placeholders = string.Join(",",
            Enumerable.Range(0, derivedArtifactIds.Count).Select(i => $"$d{i}"));

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT da.id, r.name, da.file_name, da.derived_size_bytes, da.hashed_derived_sha1
            FROM derived_artifacts da
            JOIN release_content_links rcl ON rcl.content_identity_key = da.content_identity_key
            JOIN releases              r   ON r.id = rcl.release_id
            WHERE da.id IN ({placeholders})
              AND r.status != 'unwanted'
            ORDER BY r.name, da.file_name
            """;
        for (int i = 0; i < derivedArtifactIds.Count; i++)
            cmd.Parameters.AddWithValue($"$d{i}", derivedArtifactIds[i]);

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
    /// Returns all non-unwanted derived artifacts in this DAT-line store with the
    /// file path and hash info needed by AppendVolumePlanner.
    /// </summary>
    public List<AppendCandidateInfo> GetAllWantedArtifactInfos()
    {
        var result   = new List<AppendCandidateInfo>();
        var seenDaId = new HashSet<string>(StringComparer.Ordinal);

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT da.id, da.content_identity_key, r.name, da.file_name,
                   da.relative_path, da.derived_size_bytes, da.hashed_derived_sha1
            FROM derived_artifacts da
            JOIN release_content_links rcl ON rcl.content_identity_key = da.content_identity_key
            JOIN releases              r   ON r.id = rcl.release_id
            WHERE r.status != 'unwanted'
              AND NOT EXISTS (
                SELECT 1 FROM release_content_links rcl2
                JOIN releases r2 ON r2.id = rcl2.release_id
                WHERE rcl2.content_identity_key = da.content_identity_key
                  AND r2.status = 'unwanted'
              )
            ORDER BY r.name, da.file_name
            """;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var daId = r.GetString(0);
            if (!seenDaId.Add(daId)) continue;
            result.Add(new AppendCandidateInfo
            {
                DerivedArtifactId  = daId,
                ContentIdentityKey = r.GetString(1),
                ReleaseName        = r.GetString(2),
                FileName           = r.GetString(3),
                RelativePath       = r.GetString(4),
                SizeBytes          = r.GetInt64(5),
                ExpectedSha1       = r.IsDBNull(6) ? "" : r.GetString(6),
            });
        }

        return result;
    }

    /// <summary>
    /// Returns the number of derived artifacts where at least one linked release is unwanted.
    /// Used by AppendVolumePlanner for ReleaseUnwanted diagnostics.
    /// </summary>
    public int GetUnwantedArtifactCount()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(DISTINCT da.id)
            FROM derived_artifacts da
            WHERE EXISTS (
                SELECT 1 FROM release_content_links rcl
                JOIN releases r ON r.id = rcl.release_id
                WHERE rcl.content_identity_key = da.content_identity_key
                  AND r.status = 'unwanted'
            )
            """;
        return (int)(long)(cmd.ExecuteScalar() ?? 0L);
    }

    // ── Archive artifact info (for LocalArchiveVerifyService) ────────────────────

    /// <summary>
    /// Information about a derived artifact in the active archive, including
    /// whether any linked release is unwanted (UNWANTED WINS semantics).
    /// </summary>
    public sealed record ArchiveArtifactInfo(
        string DerivedArtifactId,
        string ContentIdentityKey,
        string FileName,
        string RelativePath,
        long   SizeBytes,
        string ExpectedSha1,
        bool   IsUnwanted);

    /// <summary>
    /// Returns all derived artifacts for this DAT-line store, each annotated with
    /// whether any linked release is unwanted.  Used by LocalArchiveVerifyService.
    /// </summary>
    public List<ArchiveArtifactInfo> GetAllArchiveArtifactInfos()
    {
        var result   = new List<ArchiveArtifactInfo>();
        var seenDaId = new HashSet<string>(StringComparer.Ordinal);

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                da.id,
                da.content_identity_key,
                da.file_name,
                da.relative_path,
                da.derived_size_bytes,
                da.hashed_derived_sha1,
                CASE WHEN EXISTS (
                    SELECT 1 FROM release_content_links rcl2
                    JOIN releases r2 ON r2.id = rcl2.release_id
                    WHERE rcl2.content_identity_key = da.content_identity_key
                      AND r2.status = 'unwanted'
                ) THEN 1 ELSE 0 END AS is_unwanted
            FROM derived_artifacts da
            ORDER BY da.file_name
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var daId = r.GetString(0);
            if (!seenDaId.Add(daId)) continue;
            result.Add(new ArchiveArtifactInfo(
                DerivedArtifactId  : daId,
                ContentIdentityKey : r.GetString(1),
                FileName           : r.GetString(2),
                RelativePath       : r.GetString(3),
                SizeBytes          : r.GetInt64(4),
                ExpectedSha1       : r.IsDBNull(5) ? "" : r.GetString(5),
                IsUnwanted         : r.GetInt32(6) != 0));
        }
        return result;
    }

    /// <summary>
    /// Removes a derived_artifact row and ALL its release_content_links in one transaction.
    /// Used by LocalArchiveRepair when exclusively-unwanted artifacts are cleaned.
    /// </summary>
    public void DeleteDerivedArtifactAndLinks(string derivedArtifactId, string contentIdentityKey)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM release_content_links WHERE content_identity_key = $cik";
            cmd.Parameters.AddWithValue("$cik", contentIdentityKey);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM derived_artifacts WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", derivedArtifactId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ── Integrity validation ──────────────────────────────────────────────────

    /// <summary>
    /// Returns every release whose status is "present" but has at least one linked
    /// derived_artifact that is not "present". Used by integrity validation (Check 3).
    /// </summary>
    public sealed record ReleaseArtifactIssue(
        string ReleaseId,
        string ReleaseName,
        string ArtifactFileName,
        string ArtifactStatus);

    public List<ReleaseArtifactIssue> GetPresentReleasesWithMissingArtifacts()
    {
        var result = new List<ReleaseArtifactIssue>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT r.id, r.name, da.file_name, da.status
            FROM releases r
            JOIN release_content_links rcl ON rcl.release_id = r.id
            JOIN derived_artifacts da ON da.content_identity_key = rcl.content_identity_key
            WHERE r.status = 'present' AND da.status != 'present'
            ORDER BY r.name, da.file_name
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new ReleaseArtifactIssue(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3)));
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────

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
            SELECT da.id, r.name, da.file_name, da.relative_path, da.derived_size_bytes,
                   da.hashed_derived_sha1
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
                ExpectedSha1      = r.GetString(5),
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
        ArchiveTier        = r.IsDBNull(13) ? "B"  : r.GetString(13),
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

    // ── Verify ALL support ────────────────────────────────────────────────────

    /// <summary>Returns (Id, Status) for every derived artifact in this DAT-line store.</summary>
    public List<(string Id, string Status)> GetAllDerivedArtifactStatuses()
    {
        var result = new List<(string, string)>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, status FROM derived_artifacts";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((r.GetString(0), r.GetString(1)));
        return result;
    }

    /// <summary>Full verify info including RelativePath — used by Verify ALL for the Local Archive phase.</summary>
    public sealed record LocalArchiveVerifyInfo(
        string DerivedArtifactId,
        string ReleaseName,
        string FileName,
        string RelativePath,
        long   SizeBytes,
        string Sha1);

    public List<LocalArchiveVerifyInfo> GetLocalArchiveVerifyInfos(IReadOnlyList<string> daIds)
    {
        if (daIds.Count == 0) return [];
        var placeholders = string.Join(",", Enumerable.Range(0, daIds.Count).Select(i => $"$d{i}"));
        var result = new List<LocalArchiveVerifyInfo>(daIds.Count);
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT da.id, r.name, da.file_name, da.relative_path, da.derived_size_bytes, da.hashed_derived_sha1
            FROM derived_artifacts da
            JOIN release_content_links rcl ON rcl.content_identity_key = da.content_identity_key
            JOIN releases              r   ON r.id = rcl.release_id
            WHERE da.id IN ({placeholders})
            ORDER BY r.name, da.file_name
            """;
        for (int i = 0; i < daIds.Count; i++)
            cmd.Parameters.AddWithValue($"$d{i}", daIds[i]);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var daId = r.GetString(0);
            if (!seen.Add(daId)) continue;
            result.Add(new LocalArchiveVerifyInfo(
                DerivedArtifactId: daId,
                ReleaseName:       r.GetString(1),
                FileName:          r.GetString(2),
                RelativePath:      r.GetString(3),
                SizeBytes:         r.GetInt64(4),
                Sha1:              r.GetString(5)));
        }
        return result;
    }

    // ── Metadata field state ──────────────────────────────────────────────────

    /// <summary>
    /// Upserts the source, provider, locked flag, and timestamp for a single metadata field.
    /// </summary>
    public void SaveMetadataFieldState(
        string releaseId, string field, string source, string provider, bool locked)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO release_metadata_field_state(
                release_id, field, source, provider, locked, updated_at_utc)
            VALUES($releaseId, $field, $source, $provider, $locked, $updatedAt)
            ON CONFLICT(release_id, field) DO UPDATE SET
                source         = excluded.source,
                provider       = excluded.provider,
                locked         = excluded.locked,
                updated_at_utc = excluded.updated_at_utc
            """;
        cmd.Parameters.AddWithValue("$releaseId",  releaseId);
        cmd.Parameters.AddWithValue("$field",      field);
        cmd.Parameters.AddWithValue("$source",     source);
        cmd.Parameters.AddWithValue("$provider",   provider);
        cmd.Parameters.AddWithValue("$locked",     locked ? 1 : 0);
        cmd.Parameters.AddWithValue("$updatedAt",  DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns all field-state rows for the given release.</summary>
    public List<MetadataFieldStateRecord> LoadMetadataFieldStates(string releaseId)
    {
        var list = new List<MetadataFieldStateRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT field, source, provider, locked, updated_at_utc
            FROM release_metadata_field_state
            WHERE release_id = $releaseId
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MetadataFieldStateRecord(
                ReleaseId:    releaseId,
                Field:        reader.GetString(0),
                Source:       reader.IsDBNull(1) ? "" : reader.GetString(1),
                Provider:     reader.IsDBNull(2) ? "" : reader.GetString(2),
                Locked:       reader.GetInt64(3) != 0,
                UpdatedAtUtc: reader.IsDBNull(4) ? "" : reader.GetString(4)));
        }
        return list;
    }

    /// <summary>Returns true when the given field has locked = 1 for this release.</summary>
    public bool IsMetadataFieldLocked(string releaseId, string field)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT locked FROM release_metadata_field_state
            WHERE release_id = $releaseId AND field = $field
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        cmd.Parameters.AddWithValue("$field",     field);
        var result = cmd.ExecuteScalar();
        return result is long l && l != 0;
    }

    /// <summary>
    /// Sets or clears the locked flag on an existing field-state row.
    /// If no row exists yet, inserts one with empty source/provider.
    /// </summary>
    public void SetMetadataFieldLocked(string releaseId, string field, bool locked)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO release_metadata_field_state(
                release_id, field, source, provider, locked, updated_at_utc)
            VALUES($releaseId, $field, '', '', $locked, $updatedAt)
            ON CONFLICT(release_id, field) DO UPDATE SET
                locked         = excluded.locked,
                updated_at_utc = excluded.updated_at_utc
            """;
        cmd.Parameters.AddWithValue("$releaseId",  releaseId);
        cmd.Parameters.AddWithValue("$field",      field);
        cmd.Parameters.AddWithValue("$locked",     locked ? 1 : 0);
        cmd.Parameters.AddWithValue("$updatedAt",  DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    // ── Metadata proposals ────────────────────────────────────────────────────

    /// <summary>Upserts a single per-field proposal from a provider.</summary>
    public void SaveMetadataProposal(
        string releaseId, string provider, string field, string value)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO release_metadata_proposals(
                release_id, provider, field, value, scraped_at, accepted)
            VALUES($releaseId, $provider, $field, $value, $scrapedAt, 0)
            ON CONFLICT(release_id, provider, field) DO UPDATE SET
                value      = excluded.value,
                scraped_at = excluded.scraped_at,
                accepted   = 0
            """;
        cmd.Parameters.AddWithValue("$releaseId",  releaseId);
        cmd.Parameters.AddWithValue("$provider",   provider);
        cmd.Parameters.AddWithValue("$field",      field);
        cmd.Parameters.AddWithValue("$value",      value);
        cmd.Parameters.AddWithValue("$scrapedAt",  DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Batch-upserts all field/value pairs from a provider in a single transaction.
    /// </summary>
    public void SaveMetadataProposals(
        string releaseId, string provider, IReadOnlyDictionary<string, string> fields)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();
        var scrapedAt  = DateTime.UtcNow.ToString("o");
        foreach (var (field, value) in fields)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO release_metadata_proposals(
                    release_id, provider, field, value, scraped_at, accepted)
                VALUES($releaseId, $provider, $field, $value, $scrapedAt, 0)
                ON CONFLICT(release_id, provider, field) DO UPDATE SET
                    value      = excluded.value,
                    scraped_at = excluded.scraped_at,
                    accepted   = 0
                """;
            cmd.Parameters.AddWithValue("$releaseId",  releaseId);
            cmd.Parameters.AddWithValue("$provider",   provider);
            cmd.Parameters.AddWithValue("$field",      field);
            cmd.Parameters.AddWithValue("$value",      value);
            cmd.Parameters.AddWithValue("$scrapedAt",  scrapedAt);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Returns all proposals for a specific provider/release pair.</summary>
    public List<MetadataProposalRecord> LoadMetadataProposals(string releaseId, string provider)
    {
        var list = new List<MetadataProposalRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT field, value, scraped_at, accepted
            FROM release_metadata_proposals
            WHERE release_id = $releaseId AND provider = $provider
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        cmd.Parameters.AddWithValue("$provider",  provider);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MetadataProposalRecord(
                ReleaseId: releaseId,
                Provider:  provider,
                Field:     reader.GetString(0),
                Value:     reader.IsDBNull(1) ? "" : reader.GetString(1),
                ScrapedAt: reader.IsDBNull(2) ? "" : reader.GetString(2),
                Accepted:  reader.GetInt64(3) != 0));
        }
        return list;
    }

    /// <summary>Returns all proposals for a release across all providers.</summary>
    public List<MetadataProposalRecord> LoadAllMetadataProposals(string releaseId)
    {
        var list = new List<MetadataProposalRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT provider, field, value, scraped_at, accepted
            FROM release_metadata_proposals
            WHERE release_id = $releaseId
            ORDER BY provider, field
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MetadataProposalRecord(
                ReleaseId: releaseId,
                Provider:  reader.GetString(0),
                Field:     reader.GetString(1),
                Value:     reader.IsDBNull(2) ? "" : reader.GetString(2),
                ScrapedAt: reader.IsDBNull(3) ? "" : reader.GetString(3),
                Accepted:  reader.GetInt64(4) != 0));
        }
        return list;
    }

    /// <summary>Marks a single proposal accepted without altering release_metadata.</summary>
    public void MarkMetadataProposalAccepted(string releaseId, string provider, string field)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE release_metadata_proposals
            SET accepted = 1
            WHERE release_id = $releaseId AND provider = $provider AND field = $field
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        cmd.Parameters.AddWithValue("$provider",  provider);
        cmd.Parameters.AddWithValue("$field",     field);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes all proposals for a provider/release pair (e.g. before re-scraping).</summary>
    public void DeleteMetadataProposals(string releaseId, string provider)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM release_metadata_proposals
            WHERE release_id = $releaseId AND provider = $provider
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        cmd.Parameters.AddWithValue("$provider",  provider);
        cmd.ExecuteNonQuery();
    }

    // ── Merge-dialog: apply user-selected proposals ───────────────────────────

    /// <summary>
    /// Applies the user-selected (field, value) pairs from a provider to the canonical
    /// release_metadata. For each selection, updates the canonical value, writes
    /// field_state(source=provider, locked=false), and marks the proposal accepted.
    /// Fields not in <paramref name="selections"/> are untouched.
    /// Returns the updated <see cref="ReleaseMetadataRecord"/>.
    /// </summary>
    public ReleaseMetadataRecord ApplyMergeSelections(
        string releaseId,
        string provider,
        IReadOnlyList<(string Field, string Value)> selections,
        ReleaseMetadataRecord current)
    {
        if (selections.Count == 0)
            return current;

        var fieldValues = GetFieldValues(current);
        foreach (var (field, value) in selections)
        {
            fieldValues[field] = value;
            SaveMetadataFieldState(releaseId, field, provider, provider, locked: false);
            MarkMetadataProposalAccepted(releaseId, provider, field);
        }

        var merged = BuildRecord(releaseId, fieldValues, current.ScrapedAtUtc);
        SaveReleaseMetadata(merged);
        return merged;
    }

    // ── Provider proposal application ────────────────────────────────────────

    /// <summary>
    /// Saves provider proposals and optionally auto-applies them to empty unlocked canonical fields.
    /// <para>
    /// Rules applied to every non-empty proposed value:
    /// <list type="bullet">
    ///   <item>Field is locked or canonical value is non-empty → save proposal only (accepted=0).</item>
    ///   <item>Canonical value empty + not locked + <paramref name="autoApplyEmptyFields"/> true →
    ///         apply to release_metadata, field_state(source=provider, locked=false), proposal accepted=1.</item>
    ///   <item>Canonical value empty + not locked + <paramref name="autoApplyEmptyFields"/> false →
    ///         save proposal only (accepted=0); canonical and field_state unchanged.</item>
    /// </list>
    /// </para>
    /// Returns the merged <see cref="ReleaseMetadataRecord"/> and the set of auto-applied field names.
    /// When <paramref name="autoApplyEmptyFields"/> is false the merged record equals
    /// <paramref name="current"/> and the auto-applied set is always empty.
    /// </summary>
    public (ReleaseMetadataRecord Merged, HashSet<string> AutoApplied) ApplyProviderProposals(
        string releaseId,
        string provider,
        IReadOnlyDictionary<string, string> proposed,
        ReleaseMetadataRecord current,
        bool autoApplyEmptyFields = true)
    {
        if (proposed.Count == 0)
            return (current, []);

        var autoApplied = new HashSet<string>(StringComparer.Ordinal);
        var lockedFields = LoadMetadataFieldStates(releaseId)
            .Where(s => s.Locked)
            .Select(s => s.Field)
            .ToHashSet(StringComparer.Ordinal);
        var fieldValues = GetFieldValues(current);

        foreach (var (field, value) in proposed)
        {
            if (value.Length == 0) continue;

            var canonical = fieldValues.GetValueOrDefault(field, "");

            if (lockedFields.Contains(field) || canonical.Length > 0 || !autoApplyEmptyFields)
            {
                SaveMetadataProposal(releaseId, provider, field, value);
            }
            else
            {
                fieldValues[field] = value;
                autoApplied.Add(field);
                SaveMetadataProposal(releaseId, provider, field, value);
                MarkMetadataProposalAccepted(releaseId, provider, field);
                SaveMetadataFieldState(releaseId, field, provider, provider, locked: false);
            }
        }

        if (autoApplied.Count == 0)
            return (current, autoApplied);

        var merged = BuildRecord(releaseId, fieldValues, DateTime.UtcNow.ToString("o"));
        SaveReleaseMetadata(merged);
        return (merged, autoApplied);
    }

    private static Dictionary<string, string> GetFieldValues(ReleaseMetadataRecord r) =>
        new(StringComparer.Ordinal)
        {
            ["title"]            = r.Title,
            ["original_title"]   = r.OriginalTitle,
            ["sort_title"]       = r.SortTitle,
            ["developer"]        = r.Developer,
            ["publisher"]        = r.Publisher,
            ["year"]             = r.Year,
            ["languages"]        = r.Languages,
            ["alternate_titles"] = r.AlternateTitles,
            ["description"]      = r.Description,
            ["genre"]            = r.Genre,
            ["subgenre"]         = r.Subgenre,
            ["players"]          = r.Players,
            ["release_type"]     = r.ReleaseType,
            ["rating"]           = r.Rating,
            ["notes"]            = r.Notes,
        };

    private static ReleaseMetadataRecord BuildRecord(
        string releaseId, Dictionary<string, string> f, string scrapedAtUtc) =>
        new()
        {
            ReleaseId       = releaseId,
            Title           = f.GetValueOrDefault("title",            ""),
            OriginalTitle   = f.GetValueOrDefault("original_title",   ""),
            SortTitle       = f.GetValueOrDefault("sort_title",       ""),
            Developer       = f.GetValueOrDefault("developer",        ""),
            Publisher       = f.GetValueOrDefault("publisher",        ""),
            Year            = f.GetValueOrDefault("year",             ""),
            Languages       = f.GetValueOrDefault("languages",        ""),
            AlternateTitles = f.GetValueOrDefault("alternate_titles", ""),
            Description     = f.GetValueOrDefault("description",      ""),
            Genre           = f.GetValueOrDefault("genre",            ""),
            Subgenre        = f.GetValueOrDefault("subgenre",         ""),
            Players         = f.GetValueOrDefault("players",          ""),
            ReleaseType     = f.GetValueOrDefault("release_type",     ""),
            Rating          = f.GetValueOrDefault("rating",           ""),
            Notes           = f.GetValueOrDefault("notes",            ""),
            ScrapedAtUtc    = scrapedAtUtc,
        };

    // ── release_extra_notes ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the user-curated extra notes for the release, or null when none exist.
    /// Extra notes are Arkadia-owned and are never overwritten by provider scrapes or cache imports.
    /// </summary>
    public string? GetReleaseExtraNotes(string releaseId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT notes FROM release_extra_notes WHERE release_id = $id";
        cmd.Parameters.AddWithValue("$id", releaseId);
        var value = cmd.ExecuteScalar();
        if (value is null or DBNull) return null;
        var text = (string)value;
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Saves user-curated extra notes for the release.
    /// Whitespace-only or null input deletes the row (keeps DB clean).
    /// created_at is preserved on updates; updated_at is always refreshed.
    /// </summary>
    public void SaveReleaseExtraNotes(string releaseId, string? notes)
    {
        var trimmed = notes?.Trim() ?? "";
        using var conn = Open();

        if (trimmed.Length == 0)
        {
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM release_extra_notes WHERE release_id = $id";
            del.Parameters.AddWithValue("$id", releaseId);
            del.ExecuteNonQuery();
            return;
        }

        var now = DateTime.UtcNow.ToString("o");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO release_extra_notes(release_id, notes, created_at, updated_at)
            VALUES ($id, $notes, $now, $now)
            ON CONFLICT(release_id) DO UPDATE SET
                notes      = excluded.notes,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id",    releaseId);
        cmd.Parameters.AddWithValue("$notes", trimmed);
        cmd.Parameters.AddWithValue("$now",   now);
        cmd.ExecuteNonQuery();
    }

    // ── release_media_curation ────────────────────────────────────────────────

    public IReadOnlyList<MediaCurationRow> LoadMediaCurationRows(string releaseId)
    {
        var list = new List<MediaCurationRow>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT release_id, media_type, file_path, file_sha256,
                   is_preferred, is_excluded, excluded_reason, credits, notes
            FROM release_media_curation
            WHERE release_id = $releaseId
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new MediaCurationRow(
                ReleaseId:      r.GetString(0),
                MediaType:      r.GetString(1),
                FilePath:       r.GetString(2),
                FileSha256:     r.IsDBNull(3) ? null : r.GetString(3),
                IsPreferred:    r.GetInt64(4) != 0,
                IsExcluded:     r.GetInt64(5) != 0,
                ExcludedReason: r.IsDBNull(6) ? null : r.GetString(6),
                Credits:        r.IsDBNull(7) ? null : r.GetString(7),
                Notes:          r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    public void UpsertMediaCurationRow(MediaCurationRow row)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO release_media_curation
                (release_id, media_type, file_path, file_sha256,
                 is_preferred, is_excluded, excluded_reason, credits, notes,
                 created_at, updated_at)
            VALUES
                ($releaseId, $mediaType, $filePath, $sha256,
                 $isPref, $isExcl, $reason, $credits, $notes,
                 $now, $now)
            ON CONFLICT(release_id, media_type, file_path) DO UPDATE SET
                file_sha256     = excluded.file_sha256,
                is_preferred    = excluded.is_preferred,
                is_excluded     = excluded.is_excluded,
                excluded_reason = excluded.excluded_reason,
                credits         = excluded.credits,
                notes           = excluded.notes,
                updated_at      = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$releaseId", row.ReleaseId);
        cmd.Parameters.AddWithValue("$mediaType", row.MediaType);
        cmd.Parameters.AddWithValue("$filePath",  row.FilePath);
        cmd.Parameters.AddWithValue("$sha256",    (object?)row.FileSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$isPref",    row.IsPreferred ? 1 : 0);
        cmd.Parameters.AddWithValue("$isExcl",    row.IsExcluded  ? 1 : 0);
        cmd.Parameters.AddWithValue("$reason",    (object?)row.ExcludedReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$credits",   (object?)row.Credits  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$notes",     (object?)row.Notes    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now",       now);
        cmd.ExecuteNonQuery();
    }

    public void ClearPreferredForType(string releaseId, string mediaType)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE release_media_curation
            SET is_preferred = 0, updated_at = $now
            WHERE release_id = $releaseId AND media_type = $mediaType
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        cmd.Parameters.AddWithValue("$mediaType", mediaType);
        cmd.Parameters.AddWithValue("$now",       DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    // Atomically clears is_preferred for all rows of release_id+media_type, then marks
    // the specified file as preferred in a single transaction. Existing credits, notes,
    // excluded state, and sha256 are preserved if the row already exists.
    public void SetPreferredMediaCuration(string releaseId, string mediaType, string filePath)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = Open();
        using var tx   = conn.BeginTransaction();
        try
        {
            using var clear = conn.CreateCommand();
            clear.Transaction = tx;
            clear.CommandText = """
                UPDATE release_media_curation
                SET is_preferred = 0, updated_at = $now
                WHERE release_id = $releaseId AND media_type = $mediaType
                """;
            clear.Parameters.AddWithValue("$releaseId", releaseId);
            clear.Parameters.AddWithValue("$mediaType", mediaType);
            clear.Parameters.AddWithValue("$now",       now);
            clear.ExecuteNonQuery();

            using var upsert = conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO release_media_curation
                    (release_id, media_type, file_path, file_sha256,
                     is_preferred, is_excluded, excluded_reason, credits, notes,
                     created_at, updated_at)
                VALUES
                    ($releaseId, $mediaType, $filePath, NULL,
                     1, 0, NULL, NULL, NULL,
                     $now, $now)
                ON CONFLICT(release_id, media_type, file_path) DO UPDATE SET
                    is_preferred    = 1,
                    file_sha256     = release_media_curation.file_sha256,
                    is_excluded     = release_media_curation.is_excluded,
                    excluded_reason = release_media_curation.excluded_reason,
                    credits         = release_media_curation.credits,
                    notes           = release_media_curation.notes,
                    updated_at      = $now
                """;
            upsert.Parameters.AddWithValue("$releaseId", releaseId);
            upsert.Parameters.AddWithValue("$mediaType", mediaType);
            upsert.Parameters.AddWithValue("$filePath",  filePath);
            upsert.Parameters.AddWithValue("$now",       now);
            upsert.ExecuteNonQuery();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public void DeleteMediaCurationRow(string releaseId, string mediaType, string filePath)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM release_media_curation
            WHERE release_id = $releaseId
              AND media_type  = $mediaType
              AND file_path   = $filePath
            """;
        cmd.Parameters.AddWithValue("$releaseId", releaseId);
        cmd.Parameters.AddWithValue("$mediaType", mediaType);
        cmd.Parameters.AddWithValue("$filePath",  filePath);
        cmd.ExecuteNonQuery();
    }

    // ── Volume full-scan support ──────────────────────────────────────────────

    /// <summary>
    /// Finds a derived artifact by its observed SHA1 hash.
    /// Returns the artifact ID, canonical file name, and whether
    /// the owning release has status 'unwanted'.
    /// Returns null if no artifact with this SHA1 is recorded.
    /// </summary>
    public (string DerivedArtifactId, string FileName, bool IsUnwanted)? FindArtifactBySha1(string sha1)
    {
        if (sha1.Length == 0) return null;

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT da.id, da.file_name,
                   COALESCE(
                       (SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END
                        FROM release_content_links rcl2
                        JOIN releases r2 ON r2.id = rcl2.release_id
                        WHERE rcl2.content_identity_key = da.content_identity_key
                          AND r2.status = 'unwanted'),
                       0
                   ) AS is_unwanted
            FROM derived_artifacts da
            WHERE da.hashed_derived_sha1 = $sha1
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$sha1", sha1);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetString(0), r.GetString(1), r.GetInt32(2) != 0);
    }

    // ── Purge support ─────────────────────────────────────────────────────────

    /// <summary>
    /// Hard-deletes a single derived_artifact row by ID.
    /// Call only after the physical file has been confirmed absent.
    /// </summary>
    public void DeleteDerivedArtifactRow(string derivedArtifactId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM derived_artifacts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", derivedArtifactId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Hard-deletes all release_content_links rows for the given release.
    /// Call as part of Purge after all derived artifacts have been removed.
    /// </summary>
    public void DeleteReleaseContentLinks(string releaseId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM release_content_links WHERE release_id = $rid";
        cmd.Parameters.AddWithValue("$rid", releaseId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Sets show_in_catalog for the given release. 1 = visible, 0 = hidden.
    /// </summary>
    public void SetShowInCatalog(string releaseId, bool visible)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE releases SET show_in_catalog = $v WHERE id = $id";
        cmd.Parameters.AddWithValue("$v",  visible ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", releaseId);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
