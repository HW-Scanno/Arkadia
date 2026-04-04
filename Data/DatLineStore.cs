using System;
using System.Collections.Generic;
using System.IO;
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
                id          TEXT PRIMARY KEY,
                dat_line_id TEXT NOT NULL,
                name        TEXT NOT NULL,
                status      TEXT NOT NULL DEFAULT 'missing',
                tier        TEXT,
                region      TEXT,
                languages   TEXT,
                format      TEXT,
                size        TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_releases_name ON releases(name);

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
            """;
        cmd.ExecuteNonQuery();
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
                INSERT INTO releases(id, dat_line_id, name, status, tier, region, languages, format, size)
                VALUES($id, $datLineId, $name, $status, $tier, $region, $languages, $format, $size)
                """;
            cmd.Parameters.AddWithValue("$id",        r.Id);
            cmd.Parameters.AddWithValue("$datLineId", r.DatLineId);
            cmd.Parameters.AddWithValue("$name",      r.Name);
            cmd.Parameters.AddWithValue("$status",    r.Status);
            cmd.Parameters.AddWithValue("$tier",      r.Tier);
            cmd.Parameters.AddWithValue("$region",    r.Region);
            cmd.Parameters.AddWithValue("$languages", r.Languages);
            cmd.Parameters.AddWithValue("$format",    r.Format);
            cmd.Parameters.AddWithValue("$size",      r.Size);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<ReleaseRecord> LoadReleases()
    {
        var list = new List<ReleaseRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, dat_line_id, name, status, tier, region, languages, format, size
            FROM releases
            ORDER BY name
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new ReleaseRecord
            {
                Id        = reader.GetString(0),
                DatLineId = reader.GetString(1),
                Name      = reader.GetString(2),
                Status    = reader.GetString(3),
                Tier      = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Region    = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Languages = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Format    = reader.IsDBNull(7) ? "" : reader.GetString(7),
                Size      = reader.IsDBNull(8) ? "" : reader.GetString(8),
            });
        return list;
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

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
