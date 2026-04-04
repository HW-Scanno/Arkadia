using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Arkadia.Data;

/// <summary>
/// SQLite-backed catalog persistence.
/// Database: data/catalog.db
/// Schema is created automatically on first run.
/// </summary>
public sealed class CatalogService
{
    private readonly string _connectionString;
    private readonly string _dataDir;

    public CatalogService(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "catalog.db");
        _connectionString = $"Data Source={dbPath}";
        EnsureSchema();
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS hardware_types (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                sort_order INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS platforms (
                id                  TEXT PRIMARY KEY,
                name                TEXT NOT NULL,
                manufacturer        TEXT NOT NULL,
                hardware_type_id    TEXT,
                year_of_release     TEXT,
                media               TEXT,
                notes               TEXT,
                cpu                 TEXT,
                memory              TEXT,
                graphics            TEXT,
                sound               TEXT,
                display_resolution  TEXT,
                aspect_ratio        TEXT
            );

            CREATE TABLE IF NOT EXISTS storage_strategies (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                description TEXT,
                sort_order  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS dat_lines (
                id                   TEXT PRIMARY KEY,
                platform_id          TEXT NOT NULL,
                name                 TEXT NOT NULL,
                authority            TEXT NOT NULL,
                dat_category         TEXT NOT NULL DEFAULT '',
                version              TEXT,
                storage_strategy_id  TEXT,
                data_store_path      TEXT NOT NULL DEFAULT '',
                release_count        INTEGER NOT NULL DEFAULT 0,
                imported_at_utc      TEXT NOT NULL,
                FOREIGN KEY(platform_id) REFERENCES platforms(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_dat_lines_platform_id ON dat_lines(platform_id);
            """;
        cmd.ExecuteNonQuery();

        // ── Migrations for existing databases ────────────────────────────────
        // Rename hardware_type (free text) → hardware_type_id (FK to hardware_types)
        RunMigration(conn, "ALTER TABLE platforms ADD COLUMN hardware_type_id TEXT");
        // Migrate any existing free-text value: match by lowercase id
        RunMigration(conn, """
            UPDATE platforms
            SET    hardware_type_id = LOWER(hardware_type)
            WHERE  hardware_type IS NOT NULL
              AND  hardware_type_id IS NULL
            """);
        RunMigration(conn, "ALTER TABLE platforms DROP COLUMN hardware_type");

        // ── Migrations ────────────────────────────────────────────────────────
        RunMigration(conn, "ALTER TABLE dat_lines ADD COLUMN storage_strategy_id TEXT");
        RunMigration(conn, "ALTER TABLE dat_lines ADD COLUMN data_store_path TEXT NOT NULL DEFAULT ''");
        RunMigration(conn, "ALTER TABLE dat_lines ADD COLUMN dat_category TEXT NOT NULL DEFAULT ''"  );

        // ── Seed storage_strategies if empty ──────────────────────────────────
        using var stratCheck = conn.CreateCommand();
        stratCheck.CommandText = "SELECT COUNT(*) FROM storage_strategies";
        var stratCount = (long)(stratCheck.ExecuteScalar() ?? 0L);
        if (stratCount == 0)
        {
            using var stratSeed = conn.CreateCommand();
            stratSeed.CommandText = """
                INSERT INTO storage_strategies(id, name, description, sort_order) VALUES
                    ('none', 'No Compression', null, 10),
                    ('chd',  'CHD Compression', null, 20),
                    ('rvz',  'RVZ Compression', null, 30),
                    ('zip',  'ZIP Compression', null, 40),
                    ('7z',   '7Z Compression',  null, 50);
                """;
            stratSeed.ExecuteNonQuery();
        }

        // ── Seed hardware_types if empty ──────────────────────────────────────
        using var seedCheck = conn.CreateCommand();
        seedCheck.CommandText = "SELECT COUNT(*) FROM hardware_types";
        var count = (long)(seedCheck.ExecuteScalar() ?? 0L);
        if (count == 0)
        {
            using var seed = conn.CreateCommand();
            seed.CommandText = """
                INSERT INTO hardware_types(id, name, sort_order) VALUES
                    ('console',  'Console',  10),
                    ('handheld', 'Handheld', 20),
                    ('computer', 'Computer', 30),
                    ('arcade',   'Arcade',   40),
                    ('other',    'Other',    99);
                """;
            seed.ExecuteNonQuery();
        }
    }

    private static void RunMigration(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        try { cmd.ExecuteNonQuery(); } catch { /* already applied — safe to ignore */ }
    }

    // ── Platforms ────────────────────────────────────────────────────────────

    public List<PlatformRecord> LoadPlatforms()
    {
        var list = new List<PlatformRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.name, p.manufacturer, p.hardware_type_id,
                   p.year_of_release, p.media, p.notes,
                   p.cpu, p.memory, p.graphics, p.sound,
                   p.display_resolution, p.aspect_ratio
            FROM platforms p
            LEFT JOIN hardware_types ht ON ht.id = p.hardware_type_id
            ORDER BY
                CASE WHEN ht.sort_order IS NULL THEN 9999 ELSE ht.sort_order END,
                p.manufacturer,
                p.name
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new PlatformRecord
            {
                Id                = reader.GetString(0),
                Name              = reader.GetString(1),
                Manufacturer      = reader.GetString(2),
                HardwareTypeId    = reader.IsDBNull(3)  ? "" : reader.GetString(3),
                YearOfRelease     = reader.IsDBNull(4)  ? "" : reader.GetString(4),
                Media             = reader.IsDBNull(5)  ? "" : reader.GetString(5),
                Notes             = reader.IsDBNull(6)  ? "" : reader.GetString(6),
                Cpu               = reader.IsDBNull(7)  ? "" : reader.GetString(7),
                Memory            = reader.IsDBNull(8)  ? "" : reader.GetString(8),
                Graphics          = reader.IsDBNull(9)  ? "" : reader.GetString(9),
                Sound             = reader.IsDBNull(10) ? "" : reader.GetString(10),
                DisplayResolution = reader.IsDBNull(11) ? "" : reader.GetString(11),
                AspectRatio       = reader.IsDBNull(12) ? "" : reader.GetString(12),
            });
        return list;
    }

    public void SavePlatforms(List<PlatformRecord> records)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();
        foreach (var p in records)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO platforms(
                    id, name, manufacturer, hardware_type_id, year_of_release, media, notes,
                    cpu, memory, graphics, sound, display_resolution, aspect_ratio)
                VALUES(
                    $id, $name, $manufacturer, $hardwareTypeId, $yearOfRelease, $media, $notes,
                    $cpu, $memory, $graphics, $sound, $displayResolution, $aspectRatio)
                ON CONFLICT(id) DO UPDATE SET
                    name               = excluded.name,
                    manufacturer       = excluded.manufacturer,
                    hardware_type_id   = excluded.hardware_type_id,
                    year_of_release    = excluded.year_of_release,
                    media              = excluded.media,
                    notes              = excluded.notes,
                    cpu                = excluded.cpu,
                    memory             = excluded.memory,
                    graphics           = excluded.graphics,
                    sound              = excluded.sound,
                    display_resolution = excluded.display_resolution,
                    aspect_ratio       = excluded.aspect_ratio
                """;
            cmd.Parameters.AddWithValue("$id",                p.Id);
            cmd.Parameters.AddWithValue("$name",              p.Name);
            cmd.Parameters.AddWithValue("$manufacturer",      p.Manufacturer);
            cmd.Parameters.AddWithValue("$hardwareTypeId",    NullIfEmpty(p.HardwareTypeId));
            cmd.Parameters.AddWithValue("$yearOfRelease",     NullIfEmpty(p.YearOfRelease));
            cmd.Parameters.AddWithValue("$media",             NullIfEmpty(p.Media));
            cmd.Parameters.AddWithValue("$notes",             NullIfEmpty(p.Notes));
            cmd.Parameters.AddWithValue("$cpu",               NullIfEmpty(p.Cpu));
            cmd.Parameters.AddWithValue("$memory",            NullIfEmpty(p.Memory));
            cmd.Parameters.AddWithValue("$graphics",          NullIfEmpty(p.Graphics));
            cmd.Parameters.AddWithValue("$sound",             NullIfEmpty(p.Sound));
            cmd.Parameters.AddWithValue("$displayResolution", NullIfEmpty(p.DisplayResolution));
            cmd.Parameters.AddWithValue("$aspectRatio",       NullIfEmpty(p.AspectRatio));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public PlatformRecord? GetPlatform(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, manufacturer, hardware_type_id,
                   year_of_release, media, notes,
                   cpu, memory, graphics, sound, display_resolution, aspect_ratio
            FROM platforms WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new PlatformRecord
        {
            Id                = reader.GetString(0),
            Name              = reader.GetString(1),
            Manufacturer      = reader.GetString(2),
            HardwareTypeId    = reader.IsDBNull(3)  ? "" : reader.GetString(3),
            YearOfRelease     = reader.IsDBNull(4)  ? "" : reader.GetString(4),
            Media             = reader.IsDBNull(5)  ? "" : reader.GetString(5),
            Notes             = reader.IsDBNull(6)  ? "" : reader.GetString(6),
            Cpu               = reader.IsDBNull(7)  ? "" : reader.GetString(7),
            Memory            = reader.IsDBNull(8)  ? "" : reader.GetString(8),
            Graphics          = reader.IsDBNull(9)  ? "" : reader.GetString(9),
            Sound             = reader.IsDBNull(10) ? "" : reader.GetString(10),
            DisplayResolution = reader.IsDBNull(11) ? "" : reader.GetString(11),
            AspectRatio       = reader.IsDBNull(12) ? "" : reader.GetString(12),
        };
    }

    // ── Hardware Types ────────────────────────────────────────────────────────

    public List<HardwareTypeRecord> LoadHardwareTypes()
    {
        var list = new List<HardwareTypeRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, sort_order FROM hardware_types ORDER BY sort_order, name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new HardwareTypeRecord
            {
                Id        = reader.GetString(0),
                Name      = reader.GetString(1),
                SortOrder = reader.GetInt32(2),
            });
        return list;
    }

    public List<StorageStrategyRecord> LoadStorageStrategies()
    {
        var list = new List<StorageStrategyRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, description, sort_order FROM storage_strategies ORDER BY sort_order, name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new StorageStrategyRecord
            {
                Id          = reader.GetString(0),
                Name        = reader.GetString(1),
                Description = reader.IsDBNull(2) ? "" : reader.GetString(2),
                SortOrder   = reader.GetInt32(3),
            });
        return list;
    }

    private static object NullIfEmpty(string s) => s.Length > 0 ? s : DBNull.Value;

    // ── DAT Lines ─────────────────────────────────────────────────────────────

    public List<DatLineRecord> LoadDatLines()
    {
        var list = new List<DatLineRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, platform_id, name, authority, dat_category, version, storage_strategy_id, data_store_path, release_count, imported_at_utc
            FROM dat_lines
            ORDER BY imported_at_utc DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new DatLineRecord
            {
                Id                 = reader.GetString(0),
                PlatformId         = reader.GetString(1),
                Name               = reader.GetString(2),
                Authority          = reader.GetString(3),
                DatCategory        = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Version            = reader.IsDBNull(5) ? "" : reader.GetString(5),
                StorageStrategyId  = reader.IsDBNull(6) ? "" : reader.GetString(6),
                DataStorePath      = reader.IsDBNull(7) ? "" : reader.GetString(7),
                ReleaseCount       = reader.GetInt32(8),
                ImportedAtUtc      = DateTime.Parse(reader.GetString(9)),
            });
        return list;
    }

    public void SaveDatLines(List<DatLineRecord> records)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();
        foreach (var dl in records)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dat_lines(id, platform_id, name, authority, dat_category, version, storage_strategy_id, data_store_path, release_count, imported_at_utc)
                VALUES($id, $platformId, $name, $authority, $datCategory, $version, $storageStrategyId, $dataStorePath, $releaseCount, $importedAt)
                ON CONFLICT(id) DO UPDATE SET
                    name                 = excluded.name,
                    authority            = excluded.authority,
                    dat_category         = excluded.dat_category,
                    version              = excluded.version,
                    storage_strategy_id  = excluded.storage_strategy_id,
                    data_store_path      = excluded.data_store_path,
                    release_count        = excluded.release_count,
                    imported_at_utc      = excluded.imported_at_utc
                """;
            cmd.Parameters.AddWithValue("$id",                dl.Id);
            cmd.Parameters.AddWithValue("$platformId",        dl.PlatformId);
            cmd.Parameters.AddWithValue("$name",              dl.Name);
            cmd.Parameters.AddWithValue("$authority",         dl.Authority);
            cmd.Parameters.AddWithValue("$datCategory",       dl.DatCategory);
            cmd.Parameters.AddWithValue("$version",           dl.Version);
            cmd.Parameters.AddWithValue("$storageStrategyId", NullIfEmpty(dl.StorageStrategyId));
            cmd.Parameters.AddWithValue("$dataStorePath",     dl.DataStorePath);
            cmd.Parameters.AddWithValue("$releaseCount",      dl.ReleaseCount);
            cmd.Parameters.AddWithValue("$importedAt",        dl.ImportedAtUtc.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    public (int DatLines, int Total, int Present, int Missing, int Lost)
        GetPlatformStats(string platformId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(release_count), 0)
            FROM dat_lines
            WHERE platform_id = $pid
            """;
        cmd.Parameters.AddWithValue("$pid", platformId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0, 0, 0, 0);
        return (
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            0, 0, 0);
    }

    // ── Deletion ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the DAT line from catalog.db and deletes its per-dat-line DB file.
    /// Does not delete the platform.
    /// </summary>
    public void DeleteDatLine(string datLineId, string platformId)
    {
        // Read data_store_path before deleting so we can clean up the file.
        string? dataStorePath = null;
        using (var conn = Open())
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT data_store_path FROM dat_lines WHERE id = $id";
            sel.Parameters.AddWithValue("$id", datLineId);
            var raw = sel.ExecuteScalar() as string;
            if (raw?.Length > 0) dataStorePath = raw;

            using var del = conn.CreateCommand();
            del.CommandText = "PRAGMA foreign_keys = ON; DELETE FROM dat_lines WHERE id = $id";
            del.Parameters.AddWithValue("$id", datLineId);
            del.ExecuteNonQuery();
        }

        if (dataStorePath is not null)
        {
            var absPath = Path.Combine(_dataDir, dataStorePath);
            if (File.Exists(absPath))
                try { File.Delete(absPath); } catch { /* best-effort */ }
        }
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
