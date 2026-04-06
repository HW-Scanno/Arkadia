using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            CREATE TABLE IF NOT EXISTS disks (
                id                       TEXT PRIMARY KEY,
                label                    TEXT NOT NULL,
                status                   TEXT NOT NULL,
                declared_capacity_bytes  INTEGER NOT NULL,
                filesystem               TEXT,
                brand                    TEXT,
                model                    TEXT,
                serial                   TEXT,
                created_at               TEXT NOT NULL,
                updated_at               TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS volumes (
                id                  TEXT PRIMARY KEY,
                label               TEXT NOT NULL,
                platform_id         TEXT NOT NULL,
                dat_line_id         TEXT NOT NULL,
                status              TEXT NOT NULL,
                planned_size_bytes  INTEGER NOT NULL,
                actual_size_bytes   INTEGER NOT NULL,
                created_at          TEXT NOT NULL,
                verified_at         TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_volumes_dat_line_id ON volumes(dat_line_id);

            CREATE TABLE IF NOT EXISTS volume_locations (
                id             TEXT PRIMARY KEY,
                volume_id      TEXT NOT NULL,
                location_type  TEXT NOT NULL,
                disk_id        TEXT,
                path           TEXT,
                is_current     INTEGER NOT NULL DEFAULT 0,
                created_at     TEXT NOT NULL,
                FOREIGN KEY(volume_id) REFERENCES volumes(id) ON DELETE CASCADE,
                FOREIGN KEY(disk_id)   REFERENCES disks(id)   ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS idx_volume_locations_volume_id ON volume_locations(volume_id);
            CREATE INDEX IF NOT EXISTS idx_volume_locations_disk_id   ON volume_locations(disk_id);

            CREATE TABLE IF NOT EXISTS volume_artifacts (
                id                   TEXT PRIMARY KEY,
                volume_id            TEXT NOT NULL,
                dat_line_id          TEXT NOT NULL,
                derived_artifact_id  TEXT NOT NULL,
                status               TEXT NOT NULL,
                added_at_utc         TEXT NOT NULL,
                UNIQUE(volume_id, derived_artifact_id)
            );

            CREATE INDEX IF NOT EXISTS idx_volume_artifacts_volume  ON volume_artifacts(volume_id);
            CREATE INDEX IF NOT EXISTS idx_volume_artifacts_datline ON volume_artifacts(dat_line_id);

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
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

        // ── Seed default settings if missing ─────────────────────────────────
        using var settingSeed = conn.CreateCommand();
        settingSeed.CommandText = """
            INSERT OR IGNORE INTO settings(key, value) VALUES
                ('show_debug_artifact_info', 'false'),
                ('auto_export_ingestion_logs', 'true'),
                ('disk_sequence', '0'),
                ('log_on_copy', 'true')
            """;
        settingSeed.ExecuteNonQuery();

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

    // ── Settings ──────────────────────────────────────────────────────────────

    public string GetSetting(string key, string defaultValue = "")
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        var raw = cmd.ExecuteScalar() as string;
        return raw ?? defaultValue;
    }

    public bool GetBoolSetting(string key, bool defaultValue = false)
    {
        var raw = GetSetting(key, defaultValue ? "true" : "false");
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key",   key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
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

    /// <summary>
    /// Updates only the mutable metadata fields on an existing DAT line.
    /// Identity fields (platform_id, authority, dat_category, storage_strategy_id,
    /// data_store_path) are intentionally not touched.
    /// </summary>
    public void UpdateDatLineMetadata(string id, string version, int releaseCount, DateTime importedAtUtc)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dat_lines
            SET version         = $version,
                release_count   = $releaseCount,
                imported_at_utc = $importedAt
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id",           id);
        cmd.Parameters.AddWithValue("$version",      version);
        cmd.Parameters.AddWithValue("$releaseCount", releaseCount);
        cmd.Parameters.AddWithValue("$importedAt",   importedAtUtc.ToString("o"));
        cmd.ExecuteNonQuery();
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

    // ── Disks ─────────────────────────────────────────────────────────────────

    public List<DiskRecord> GetDisks()
    {
        var list = new List<DiskRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, status, declared_capacity_bytes, filesystem, brand, model, serial, created_at, updated_at
            FROM disks
            ORDER BY label
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadDisk(r));
        return list;
    }

    /// <summary>
    /// Atomically increments and returns the next disk sequence number, then
    /// converts it to the Arkadia label format:
    ///   1–9999  → ARKADIA-0001 … ARKADIA-9999
    ///   10000+  → ARKADIA-A001 … ARKADIA-Z999
    /// Uses the settings table key "disk_sequence".
    /// </summary>
    public string NextDiskLabel()
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT value FROM settings WHERE key = 'disk_sequence'";
        var raw = read.ExecuteScalar() as string;
        int next = raw is not null && int.TryParse(raw, out var n) ? n + 1 : 1;

        using var write = conn.CreateCommand();
        write.Transaction = tx;
        write.CommandText = """
            INSERT INTO settings(key, value) VALUES('disk_sequence', $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        write.Parameters.AddWithValue("$v", next.ToString());
        write.ExecuteNonQuery();
        tx.Commit();

        return FormatDiskLabel(next);
    }

    /// <summary>
    /// Returns what the next disk label would be WITHOUT incrementing the sequence.
    /// Use this for display-only purposes (e.g. showing a preview in the create dialog).
    /// Call <see cref="NextDiskLabel"/> only when the user confirms creation.
    /// </summary>
    public string PeekNextDiskLabel()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = 'disk_sequence'";
        var raw = cmd.ExecuteScalar() as string;
        int next = raw is not null && int.TryParse(raw, out var n) ? n + 1 : 1;
        return FormatDiskLabel(next);
    }

    private static string FormatDiskLabel(int n)
    {
        if (n <= 9999)
            return $"ARKADIA-{n:D4}";
        // Overflow: letter prefix, 3-digit suffix within that letter block
        // n=10000 → A001, n=10999 → A999, n=11000 → B001, …
        int overflow = n - 10000;
        int letter   = overflow / 999;          // 0=A … 25=Z
        int suffix   = (overflow % 999) + 1;    // 1–999
        char c       = (char)('A' + (letter % 26));
        return $"ARKADIA-{c}{suffix:D3}";
    }

    /// <summary>
    /// Updates declared_capacity_bytes and updated_at for an existing disk record.
    /// </summary>
    public void UpdateDiskCapacity(string diskId, long capacityBytes)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE disks
            SET declared_capacity_bytes = $cap,
                updated_at              = $upd
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$cap", capacityBytes);
        cmd.Parameters.AddWithValue("$upd", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id",  diskId);
        cmd.ExecuteNonQuery();
    }

    public void SaveDisk(DiskRecord d)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO disks(id, label, status, declared_capacity_bytes, filesystem, brand, model, serial, created_at, updated_at)
            VALUES($id, $label, $status, $cap, $fs, $brand, $model, $serial, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                label                   = excluded.label,
                status                  = excluded.status,
                declared_capacity_bytes = excluded.declared_capacity_bytes,
                filesystem              = excluded.filesystem,
                brand                   = excluded.brand,
                model                   = excluded.model,
                serial                  = excluded.serial,
                updated_at              = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id",      d.Id);
        cmd.Parameters.AddWithValue("$label",   d.Label);
        cmd.Parameters.AddWithValue("$status",  d.Status);
        cmd.Parameters.AddWithValue("$cap",     d.DeclaredCapacityBytes);
        cmd.Parameters.AddWithValue("$fs",      NullIfEmpty(d.Filesystem));
        cmd.Parameters.AddWithValue("$brand",   NullIfEmpty(d.Brand));
        cmd.Parameters.AddWithValue("$model",   NullIfEmpty(d.Model));
        cmd.Parameters.AddWithValue("$serial",  NullIfEmpty(d.Serial));
        cmd.Parameters.AddWithValue("$created", d.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", d.UpdatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public (long Capacity, long Used, long Free) GetDiskUsage(string diskId)
    {
        using var conn = Open();

        // capacity
        using var capCmd = conn.CreateCommand();
        capCmd.CommandText = "SELECT declared_capacity_bytes FROM disks WHERE id = $id";
        capCmd.Parameters.AddWithValue("$id", diskId);
        var capacity = (long)(capCmd.ExecuteScalar() ?? 0L);

        // used = sum of actual_size_bytes of volumes whose current location is this disk
        using var usedCmd = conn.CreateCommand();
        usedCmd.CommandText = """
            SELECT COALESCE(SUM(v.actual_size_bytes), 0)
            FROM volumes v
            JOIN volume_locations vl ON vl.volume_id = v.id
            WHERE vl.disk_id = $diskId AND vl.is_current = 1
            """;
        usedCmd.Parameters.AddWithValue("$diskId", diskId);
        var used = (long)(usedCmd.ExecuteScalar() ?? 0L);

        return (capacity, used, Math.Max(0, capacity - used));
    }

    private static DiskRecord ReadDisk(SqliteDataReader r) => new()
    {
        Id                    = r.GetString(0),
        Label                 = r.GetString(1),
        Status                = r.GetString(2),
        DeclaredCapacityBytes = r.GetInt64(3),
        Filesystem            = r.IsDBNull(4) ? "" : r.GetString(4),
        Brand                 = r.IsDBNull(5) ? "" : r.GetString(5),
        Model                 = r.IsDBNull(6) ? "" : r.GetString(6),
        Serial                = r.IsDBNull(7) ? "" : r.GetString(7),
        CreatedAt             = DateTime.Parse(r.GetString(8)),
        UpdatedAt             = DateTime.Parse(r.GetString(9)),
    };

    // ── Volumes ───────────────────────────────────────────────────────────────

    public List<VolumeRecord> GetVolumes()
    {
        var list = new List<VolumeRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, platform_id, dat_line_id, status, planned_size_bytes, actual_size_bytes, created_at, verified_at
            FROM volumes
            ORDER BY label
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadVolume(r));
        return list;
    }

    public List<VolumeRecord> GetVolumesByDisk(string diskId)
    {
        var list = new List<VolumeRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT v.id, v.label, v.platform_id, v.dat_line_id, v.status,
                   v.planned_size_bytes, v.actual_size_bytes, v.created_at, v.verified_at
            FROM volumes v
            JOIN volume_locations vl ON vl.volume_id = v.id
            WHERE vl.disk_id = $diskId AND vl.is_current = 1
            ORDER BY v.label
            """;
        cmd.Parameters.AddWithValue("$diskId", diskId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadVolume(r));
        return list;
    }

    public void SaveVolume(VolumeRecord v)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO volumes(id, label, platform_id, dat_line_id, status, planned_size_bytes, actual_size_bytes, created_at, verified_at)
            VALUES($id, $label, $platId, $dlId, $status, $planned, $actual, $created, $verified)
            ON CONFLICT(id) DO UPDATE SET
                label              = excluded.label,
                status             = excluded.status,
                planned_size_bytes = excluded.planned_size_bytes,
                actual_size_bytes  = excluded.actual_size_bytes,
                verified_at        = excluded.verified_at
            """;
        cmd.Parameters.AddWithValue("$id",       v.Id);
        cmd.Parameters.AddWithValue("$label",    v.Label);
        cmd.Parameters.AddWithValue("$platId",   v.PlatformId);
        cmd.Parameters.AddWithValue("$dlId",     v.DatLineId);
        cmd.Parameters.AddWithValue("$status",   v.Status);
        cmd.Parameters.AddWithValue("$planned",  v.PlannedSizeBytes);
        cmd.Parameters.AddWithValue("$actual",   v.ActualSizeBytes);
        cmd.Parameters.AddWithValue("$created",  v.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$verified", v.VerifiedAt.HasValue ? v.VerifiedAt.Value.ToString("o") : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public VolumeLocationRecord? GetCurrentLocation(string volumeId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, volume_id, location_type, disk_id, path, is_current, created_at
            FROM volume_locations
            WHERE volume_id = $vid AND is_current = 1
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$vid", volumeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new VolumeLocationRecord
        {
            Id           = r.GetString(0),
            VolumeId     = r.GetString(1),
            LocationType = r.GetString(2),
            DiskId       = r.IsDBNull(3) ? null : r.GetString(3),
            Path         = r.IsDBNull(4) ? null : r.GetString(4),
            IsCurrent    = r.GetInt32(5) == 1,
            CreatedAt    = DateTime.Parse(r.GetString(6)),
        };
    }

    // ── Volume Artifacts ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns all distinct derived_artifact_id values assigned (across all volumes)
    /// for the given dat_line_id. Used by the Planning preview to mark candidates
    /// as already assigned.
    /// </summary>
    public System.Collections.Generic.HashSet<string> GetAssignedDerivedIdsByDatLine(string datLineId)
    {
        var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT derived_artifact_id
            FROM volume_artifacts
            WHERE dat_line_id = $dlid
            """;
        cmd.Parameters.AddWithValue("$dlid", datLineId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            set.Add(r.GetString(0));
        return set;
    }

    public List<VolumeArtifactRecord> GetVolumeArtifacts(string volumeId)
    {
        var list = new List<VolumeArtifactRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, volume_id, dat_line_id, derived_artifact_id, status, added_at_utc
            FROM volume_artifacts
            WHERE volume_id = $vid
            ORDER BY added_at_utc
            """;
        cmd.Parameters.AddWithValue("$vid", volumeId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new VolumeArtifactRecord
            {
                Id                = r.GetString(0),
                VolumeId          = r.GetString(1),
                DatLineId         = r.GetString(2),
                DerivedArtifactId = r.GetString(3),
                Status            = r.GetString(4),
                AddedAtUtc        = DateTime.Parse(r.GetString(5)),
            });
        return list;
    }

    public bool VolumeArtifactExists(string volumeId, string derivedArtifactId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM volume_artifacts WHERE volume_id = $vid AND derived_artifact_id = $did LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$vid", volumeId);
        cmd.Parameters.AddWithValue("$did", derivedArtifactId);
        using var r = cmd.ExecuteReader();
        return r.Read();
    }

    public void SaveVolumeArtifact(VolumeArtifactRecord va)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO volume_artifacts(id, volume_id, dat_line_id, derived_artifact_id, status, added_at_utc)
            VALUES($id, $vid, $dlid, $daid, $status, $added)
            ON CONFLICT(volume_id, derived_artifact_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$id",     va.Id);
        cmd.Parameters.AddWithValue("$vid",    va.VolumeId);
        cmd.Parameters.AddWithValue("$dlid",   va.DatLineId);
        cmd.Parameters.AddWithValue("$daid",   va.DerivedArtifactId);
        cmd.Parameters.AddWithValue("$status", va.Status);
        cmd.Parameters.AddWithValue("$added",  va.AddedAtUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts all records in <paramref name="batch"/> as a single atomic transaction.
    /// Uses ON CONFLICT DO NOTHING so already-present rows are skipped without error.
    /// Returns the number of rows actually inserted.
    /// </summary>
    public int SaveVolumeArtifactsBatch(System.Collections.Generic.IReadOnlyList<VolumeArtifactRecord> batch)
    {
        if (batch.Count == 0) return 0;

        int inserted = 0;
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO volume_artifacts(id, volume_id, dat_line_id, derived_artifact_id, status, added_at_utc)
            VALUES($id, $vid, $dlid, $daid, $status, $added)
            ON CONFLICT(volume_id, derived_artifact_id) DO NOTHING
            """;
        var pId     = cmd.Parameters.Add("$id",     Microsoft.Data.Sqlite.SqliteType.Text);
        var pVid    = cmd.Parameters.Add("$vid",    Microsoft.Data.Sqlite.SqliteType.Text);
        var pDlid   = cmd.Parameters.Add("$dlid",   Microsoft.Data.Sqlite.SqliteType.Text);
        var pDaid   = cmd.Parameters.Add("$daid",   Microsoft.Data.Sqlite.SqliteType.Text);
        var pStatus = cmd.Parameters.Add("$status", Microsoft.Data.Sqlite.SqliteType.Text);
        var pAdded  = cmd.Parameters.Add("$added",  Microsoft.Data.Sqlite.SqliteType.Text);

        foreach (var va in batch)
        {
            pId.Value     = va.Id;
            pVid.Value    = va.VolumeId;
            pDlid.Value   = va.DatLineId;
            pDaid.Value   = va.DerivedArtifactId;
            pStatus.Value = va.Status;
            pAdded.Value  = va.AddedAtUtc.ToString("o");
            inserted += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return inserted;
    }

    /// <summary>
    /// Recalculates volumes.actual_size_bytes as the sum of size_bytes of all
    /// derived artifacts assigned to the volume with status = "present_in_final".
    /// The size_bytes values are read from the supplied <paramref name="sizeByDerivedId"/> map
    /// (keyed by derived_artifact_id), which the caller builds from the DAT-line DB.
    /// </summary>
    public void RecalculateVolumeActualSize(string volumeId,
        System.Collections.Generic.Dictionary<string, long> sizeByDerivedId)
    {
        var assignments = GetVolumeArtifacts(volumeId);
        long total = 0;
        foreach (var va in assignments)
        {
            if (va.Status == "present_in_final" &&
                sizeByDerivedId.TryGetValue(va.DerivedArtifactId, out var sz))
                total += sz;
        }

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE volumes SET actual_size_bytes = $sz WHERE id = $vid";
        cmd.Parameters.AddWithValue("$sz",  total);
        cmd.Parameters.AddWithValue("$vid", volumeId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Sets all existing locations for a volume to is_current = 0, then inserts the new location
    /// with is_current = 1. Maintains the one-current-location invariant.
    /// </summary>
    public void SetCurrentLocation(VolumeLocationRecord loc)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        using var clear = conn.CreateCommand();
        clear.CommandText = "UPDATE volume_locations SET is_current = 0 WHERE volume_id = $vid";
        clear.Parameters.AddWithValue("$vid", loc.VolumeId);
        clear.ExecuteNonQuery();

        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO volume_locations(id, volume_id, location_type, disk_id, path, is_current, created_at)
            VALUES($id, $volId, $locType, $diskId, $path, 1, $created)
            """;
        ins.Parameters.AddWithValue("$id",      loc.Id);
        ins.Parameters.AddWithValue("$volId",   loc.VolumeId);
        ins.Parameters.AddWithValue("$locType", loc.LocationType);
        ins.Parameters.AddWithValue("$diskId",  (object?)loc.DiskId ?? DBNull.Value);
        ins.Parameters.AddWithValue("$path",    (object?)loc.Path   ?? DBNull.Value);
        ins.Parameters.AddWithValue("$created", loc.CreatedAt.ToString("o"));
        ins.ExecuteNonQuery();

        tx.Commit();
    }

    public void SaveVolumeLocation(VolumeLocationRecord loc)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO volume_locations(id, volume_id, location_type, disk_id, path, is_current, created_at)
            VALUES($id, $volId, $locType, $diskId, $path, $isCurrent, $created)
            ON CONFLICT(id) DO UPDATE SET
                location_type = excluded.location_type,
                disk_id       = excluded.disk_id,
                path          = excluded.path,
                is_current    = excluded.is_current
            """;
        cmd.Parameters.AddWithValue("$id",        loc.Id);
        cmd.Parameters.AddWithValue("$volId",     loc.VolumeId);
        cmd.Parameters.AddWithValue("$locType",   loc.LocationType);
        cmd.Parameters.AddWithValue("$diskId",    (object?)loc.DiskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path",      (object?)loc.Path   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$isCurrent", loc.IsCurrent ? 1 : 0);
        cmd.Parameters.AddWithValue("$created",   loc.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static VolumeRecord ReadVolume(SqliteDataReader r) => new()
    {
        Id               = r.GetString(0),
        Label            = r.GetString(1),
        PlatformId       = r.GetString(2),
        DatLineId        = r.GetString(3),
        Status           = r.GetString(4),
        PlannedSizeBytes = r.GetInt64(5),
        ActualSizeBytes  = r.GetInt64(6),
        CreatedAt        = DateTime.Parse(r.GetString(7)),
        VerifiedAt       = r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8)),
    };

    // ── Connection ────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
