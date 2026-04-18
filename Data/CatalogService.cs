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
                sort_order INTEGER NOT NULL,
                is_seeded  INTEGER NOT NULL DEFAULT 0
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
                id                       TEXT PRIMARY KEY,
                platform_id              TEXT NOT NULL,
                name                     TEXT NOT NULL,
                authority                TEXT NOT NULL,
                dat_category             TEXT NOT NULL DEFAULT '',
                version                  TEXT,
                storage_strategy_id      TEXT,
                data_store_path          TEXT NOT NULL DEFAULT '',
                release_count            INTEGER NOT NULL DEFAULT 0,
                imported_at_utc          TEXT NOT NULL,
                transform_strategy_type  TEXT NOT NULL DEFAULT 'none',
                folder_transform_id      TEXT,
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
                family                   TEXT NOT NULL DEFAULT 'core',
                created_at               TEXT NOT NULL,
                updated_at               TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS volumes (
                id                  TEXT PRIMARY KEY,
                label               TEXT NOT NULL,
                platform_id         TEXT NOT NULL,
                dat_line_id         TEXT NOT NULL,
                status              TEXT NOT NULL,
                health              TEXT NOT NULL DEFAULT 'ok',
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
                content_identity_key TEXT NOT NULL DEFAULT '',
                status               TEXT NOT NULL,
                added_at_utc         TEXT NOT NULL,
                UNIQUE(volume_id, derived_artifact_id)
            );

            CREATE INDEX IF NOT EXISTS idx_volume_artifacts_volume  ON volume_artifacts(volume_id);
            CREATE INDEX IF NOT EXISTS idx_volume_artifacts_datline ON volume_artifacts(dat_line_id);

            CREATE TABLE IF NOT EXISTS authorities (
                id        TEXT PRIMARY KEY,
                name      TEXT NOT NULL,
                is_seeded INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tools (
                tool_id          TEXT PRIMARY KEY,
                folder_name      TEXT NOT NULL,
                executable_name  TEXT NOT NULL,
                is_bundled       INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS transforms (
                transform_id      TEXT PRIMARY KEY,
                name              TEXT NOT NULL,
                tool_id           TEXT,
                command_template  TEXT NOT NULL DEFAULT '',
                output_extension  TEXT NOT NULL DEFAULT '',
                is_enabled        INTEGER NOT NULL DEFAULT 1,
                transform_type    TEXT NOT NULL DEFAULT 'file_strategy'
            );

            CREATE TABLE IF NOT EXISTS dat_line_extension_transforms (
                dat_line_id    TEXT NOT NULL,
                file_extension TEXT NOT NULL,
                transform_id   TEXT,
                is_discard     INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (dat_line_id, file_extension)
            );
            """;
        cmd.ExecuteNonQuery();

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

        // ── Seed tools if empty ───────────────────────────────────────────────
        using var toolCheck = conn.CreateCommand();
        toolCheck.CommandText = "SELECT COUNT(*) FROM tools";
        if ((long)(toolCheck.ExecuteScalar() ?? 0L) == 0)
        {
            using var toolSeed = conn.CreateCommand();
            toolSeed.CommandText = """
                INSERT INTO tools(tool_id, folder_name, executable_name, is_bundled) VALUES
                    ('chdman', 'chdman', 'chdman.exe', 1),
                    ('7zip',   '7zip',   '7z.exe',     1);
                """;
            toolSeed.ExecuteNonQuery();
        }

        // ── Seed transforms if empty ──────────────────────────────────────────
        using var txCheck = conn.CreateCommand();
        txCheck.CommandText = "SELECT COUNT(*) FROM transforms";
        if ((long)(txCheck.ExecuteScalar() ?? 0L) == 0)
        {
            using var txSeed = conn.CreateCommand();
            txSeed.CommandText = """
                INSERT INTO transforms(transform_id, name, tool_id, command_template, output_extension, is_enabled, transform_type) VALUES
                    ('no_compression',       'No Compression',          null,     '',                                         '',     1, 'file_strategy'),
                    ('chd_cd_compression',   'CHD CD Compression',      'chdman', 'createcd -i "{input}" -o "{output}"',      '.chd', 1, 'file_strategy'),
                    ('chd_dvd_compression',  'CHD DVD Compression',     'chdman', 'createdvd -i "{input}" -o "{output}"',     '.chd', 1, 'file_strategy'),
                    ('chd_gd_compression',   'CHD GD Compression',      'chdman', 'createcd -i "{input}" -o "{output}"',      '.chd', 1, 'file_strategy'),
                    ('zip_compression',      'ZIP Compression (Folder)', '7zip',   'a -tzip "{output}" "{input}"',             '.zip', 1, 'folder_strategy'),
                    ('zip_file_compression', 'ZIP Compression (File)',  '7zip',   'a -tzip "{output}" "{input}"',             '.zip', 1, 'file_strategy');
                """;
            txSeed.ExecuteNonQuery();
        }

        // ── Seed default settings if missing ─────────────────────────────────
        using var settingSeed = conn.CreateCommand();
        settingSeed.CommandText = """
            INSERT OR IGNORE INTO settings(key, value) VALUES
                ('show_debug_artifact_info', 'false'),
                ('auto_export_ingestion_logs', 'true'),
                ('auto_export_verify_logs', 'true'),
                ('auto_export_repair_logs', 'true'),
                ('disk_sequence', '0'),
                ('disk_sequence_core',   '0'),
                ('disk_sequence_extras', '0'),
                ('disk_sequence_books',  '0'),
                ('log_on_copy', 'true'),
                ('quarantine_unexpected_on_verify', 'false'),
                ('quarantine_mismatch_on_verify', 'false'),
                ('image_cache_regen_write_log', 'true'),
                ('logs_to_keep_per_type', '5')
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
                INSERT INTO hardware_types(id, name, sort_order, is_seeded) VALUES
                    ('console',  'Console',  10, 1),
                    ('handheld', 'Handheld', 20, 1),
                    ('computer', 'Computer', 30, 1),
                    ('arcade',   'Arcade',   40, 1),
                    ('other',    'Other',    99, 1);
                """;
            seed.ExecuteNonQuery();
        }

        // ── Seed authorities (INSERT OR IGNORE — safe to re-run) ─────────────
        using var authSeed = conn.CreateCommand();
        authSeed.CommandText = """
            INSERT OR IGNORE INTO authorities(id, name, is_seeded) VALUES
                ('redump',  'ReDump',   1),
                ('nointro', 'No-Intro', 1),
                ('tosec',   'TOSEC',    1),
                ('custom',  'Custom',   1);
            """;
        authSeed.ExecuteNonQuery();
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
        cmd.CommandText = "SELECT id, name, sort_order, is_seeded FROM hardware_types ORDER BY sort_order, name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new HardwareTypeRecord
            {
                Id        = reader.GetString(0),
                Name      = reader.GetString(1),
                SortOrder = reader.GetInt32(2),
                IsSeeded  = reader.GetInt32(3) != 0,
            });
        return list;
    }

    public void SaveHardwareType(HardwareTypeRecord type)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO hardware_types(id, name, sort_order, is_seeded)
            VALUES($id, $name, $sort, $seeded)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name
            """;
        cmd.Parameters.AddWithValue("$id",     type.Id);
        cmd.Parameters.AddWithValue("$name",   type.Name);
        cmd.Parameters.AddWithValue("$sort",   type.SortOrder);
        cmd.Parameters.AddWithValue("$seeded", type.IsSeeded ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void DeleteHardwareType(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM hardware_types WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public bool HardwareTypeHasDependencies(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM platforms WHERE hardware_type_id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
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

    // ── Authorities ───────────────────────────────────────────────────────────

    public List<AuthorityRecord> LoadAuthorities()
    {
        var list = new List<AuthorityRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, is_seeded FROM authorities ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AuthorityRecord
            {
                Id       = r.GetString(0),
                Name     = r.GetString(1),
                IsSeeded = r.GetInt32(2) != 0,
            });
        return list;
    }

    public void SaveAuthority(AuthorityRecord authority)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO authorities(id, name, is_seeded)
            VALUES($id, $name, $seeded)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name
            """;
        cmd.Parameters.AddWithValue("$id",     authority.Id);
        cmd.Parameters.AddWithValue("$name",   authority.Name);
        cmd.Parameters.AddWithValue("$seeded", authority.IsSeeded ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void DeleteAuthority(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM authorities WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public bool AuthorityHasDependencies(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dat_lines WHERE authority = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    // ── Tools ─────────────────────────────────────────────────────────────────

    public List<ToolRecord> LoadTools()
    {
        var list = new List<ToolRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT tool_id, folder_name, executable_name, is_bundled FROM tools ORDER BY tool_id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ToolRecord
            {
                Id             = r.GetString(0),
                FolderName     = r.GetString(1),
                ExecutableName = r.GetString(2),
                IsBundled      = r.GetInt32(3) != 0,
            });
        return list;
    }

    public void SaveTool(ToolRecord tool)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO tools(tool_id, folder_name, executable_name, is_bundled)
            VALUES($id, $folder, $exe, $bundled)
            """;
        cmd.Parameters.AddWithValue("$id",      tool.Id);
        cmd.Parameters.AddWithValue("$folder",  tool.FolderName);
        cmd.Parameters.AddWithValue("$exe",     tool.ExecutableName);
        cmd.Parameters.AddWithValue("$bundled", tool.IsBundled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public bool ToolHasDependencies(string toolId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM transforms WHERE tool_id = $id";
        cmd.Parameters.AddWithValue("$id", toolId);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    public void DeleteTool(string toolId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tools WHERE tool_id = $id";
        cmd.Parameters.AddWithValue("$id", toolId);
        cmd.ExecuteNonQuery();
    }

    // ── Transforms ────────────────────────────────────────────────────────────

    public List<TransformRecord> LoadTransforms()
    {
        var list = new List<TransformRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT transform_id, name, tool_id, command_template, output_extension, is_enabled, transform_type
            FROM transforms
            ORDER BY transform_id
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TransformRecord
            {
                Id              = r.GetString(0),
                Name            = r.GetString(1),
                ToolId          = r.IsDBNull(2) ? "" : r.GetString(2),
                CommandTemplate = r.GetString(3),
                OutputExtension = r.GetString(4),
                IsEnabled       = r.GetInt32(5) != 0,
                TransformType   = r.IsDBNull(6) ? "file_strategy" : r.GetString(6),
            });
        return list;
    }

    public void SaveTransform(TransformRecord t)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transforms(transform_id, name, tool_id, command_template, output_extension, is_enabled, transform_type)
            VALUES($id, $name, $toolId, $cmd, $ext, $enabled, $type)
            ON CONFLICT(transform_id) DO UPDATE SET
                name              = excluded.name,
                tool_id           = excluded.tool_id,
                command_template  = excluded.command_template,
                output_extension  = excluded.output_extension,
                is_enabled        = excluded.is_enabled,
                transform_type    = excluded.transform_type
            """;
        cmd.Parameters.AddWithValue("$id",      t.Id);
        cmd.Parameters.AddWithValue("$name",    t.Name);
        cmd.Parameters.AddWithValue("$toolId",  t.ToolId.Length > 0 ? t.ToolId : DBNull.Value);
        cmd.Parameters.AddWithValue("$cmd",     t.CommandTemplate);
        cmd.Parameters.AddWithValue("$ext",     t.OutputExtension);
        cmd.Parameters.AddWithValue("$enabled", t.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$type",    t.TransformType);
        cmd.ExecuteNonQuery();
    }

    public bool TransformHasDependencies(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM dat_line_extension_transforms WHERE transform_id = $id
            UNION ALL
            SELECT 1 FROM dat_lines WHERE folder_transform_id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    public void DeleteTransform(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM transforms WHERE transform_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
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
            SELECT id, platform_id, name, authority, dat_category, version, storage_strategy_id, data_store_path, release_count, imported_at_utc,
                   transform_strategy_type, folder_transform_id
            FROM dat_lines
            ORDER BY imported_at_utc DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new DatLineRecord
            {
                Id                    = reader.GetString(0),
                PlatformId            = reader.GetString(1),
                Name                  = reader.GetString(2),
                Authority             = reader.GetString(3),
                DatCategory           = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Version               = reader.IsDBNull(5) ? "" : reader.GetString(5),
                StorageStrategyId     = reader.IsDBNull(6) ? "" : reader.GetString(6),
                DataStorePath         = reader.IsDBNull(7) ? "" : reader.GetString(7),
                ReleaseCount          = reader.GetInt32(8),
                ImportedAtUtc         = DateTime.Parse(reader.GetString(9)),
                TransformStrategyType = reader.IsDBNull(10) ? "none" : reader.GetString(10),
                FolderTransformId     = reader.IsDBNull(11) ? "" : reader.GetString(11),
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

    // ── Transform Strategy ────────────────────────────────────────────────────

    public void SaveDatLineTransformStrategy(string datLineId, string strategyType, string? folderTransformId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dat_lines
            SET transform_strategy_type = $type,
                folder_transform_id     = $folderId
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id",       datLineId);
        cmd.Parameters.AddWithValue("$type",     strategyType);
        cmd.Parameters.AddWithValue("$folderId", (object?)folderTransformId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<ExtensionTransformMapping> LoadExtensionMappings(string datLineId)
    {
        var list = new List<ExtensionTransformMapping>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT dat_line_id, file_extension, transform_id, is_discard
            FROM dat_line_extension_transforms
            WHERE dat_line_id = $id
            ORDER BY file_extension
            """;
        cmd.Parameters.AddWithValue("$id", datLineId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ExtensionTransformMapping
            {
                DatLineId     = r.GetString(0),
                FileExtension = r.GetString(1),
                TransformId   = r.IsDBNull(2) ? "" : r.GetString(2),
                IsDiscard     = r.GetInt32(3) != 0,
            });
        return list;
    }

    public void SaveExtensionMappings(string datLineId, List<ExtensionTransformMapping> mappings)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM dat_line_extension_transforms WHERE dat_line_id = $id";
        del.Parameters.AddWithValue("$id", datLineId);
        del.ExecuteNonQuery();

        foreach (var m in mappings)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO dat_line_extension_transforms(dat_line_id, file_extension, transform_id, is_discard)
                VALUES($datLineId, $ext, $xformId, $discard)
                """;
            ins.Parameters.AddWithValue("$datLineId", m.DatLineId);
            ins.Parameters.AddWithValue("$ext",       m.FileExtension);
            ins.Parameters.AddWithValue("$xformId",   m.TransformId.Length > 0 ? m.TransformId : (object)DBNull.Value);
            ins.Parameters.AddWithValue("$discard",   m.IsDiscard ? 1 : 0);
            ins.ExecuteNonQuery();
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

    // ── Disks ─────────────────────────────────────────────────────────────────

    public List<DiskRecord> GetDisks()
    {
        var list = new List<DiskRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, status, declared_capacity_bytes, filesystem, brand, model, serial, created_at, updated_at, family
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
    /// Atomically increments and returns the next disk label for the given family.
    /// Format: ARKADIA-{PREFIX}-XXXX  (e.g. ARKADIA-CORE-0001)
    /// Uses the settings key "disk_sequence_{family}".
    /// </summary>
    public string NextDiskLabel(string family)
    {
        var key = $"disk_sequence_{family}";
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT value FROM settings WHERE key = $key";
        read.Parameters.AddWithValue("$key", key);
        var raw = read.ExecuteScalar() as string;
        int next = raw is not null && int.TryParse(raw, out var n) ? n + 1 : 1;

        using var write = conn.CreateCommand();
        write.Transaction = tx;
        write.CommandText = """
            INSERT INTO settings(key, value) VALUES($key, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        write.Parameters.AddWithValue("$key", key);
        write.Parameters.AddWithValue("$v",   next.ToString());
        write.ExecuteNonQuery();
        tx.Commit();

        return $"ARKADIA-{FamilyToPrefix(family)}-{next:D4}";
    }

    private static string FamilyToPrefix(string family) => family switch
    {
        "extras" => "EXTRAS",
        "books"  => "BOOKS",
        _        => "CORE",
    };

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
    /// <summary>
    /// Atomically marks a disk, all its current volumes, and all volume_artifacts
    /// on those volumes as lost within a single catalog transaction.
    /// Returns the volume count and a per-dat-line map of derived artifact IDs that
    /// were marked lost — callers must propagate this to each DatLineStore separately.
    /// This is a manual administrative action — never call from runtime discovery paths.
    /// </summary>
    public (int VolumeCount, Dictionary<string, List<string>> ArtifactWork)
        MarkDiskLost(string diskId)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        // Mark disk lost
        using var diskCmd = conn.CreateCommand();
        diskCmd.Transaction = tx;
        diskCmd.CommandText = """
            UPDATE disks
            SET status = 'lost', updated_at = $now
            WHERE id = $id
            """;
        diskCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        diskCmd.Parameters.AddWithValue("$id",  diskId);
        diskCmd.ExecuteNonQuery();

        // Collect volume IDs before marking so we can run further queries in the same tx
        var volumeIds = new List<string>();
        using (var getVolCmd = conn.CreateCommand())
        {
            getVolCmd.Transaction = tx;
            getVolCmd.CommandText = """
                SELECT volume_id FROM volume_locations
                WHERE disk_id = $diskId AND is_current = 1
                """;
            getVolCmd.Parameters.AddWithValue("$diskId", diskId);
            using var rv = getVolCmd.ExecuteReader();
            while (rv.Read()) volumeIds.Add(rv.GetString(0));
        }

        int volumeCount = 0;
        var artifactWork = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (volumeIds.Count > 0)
        {
            var vp = string.Join(",", Enumerable.Range(0, volumeIds.Count).Select(i => $"$v{i}"));

            // Mark volumes lost
            using var volCmd = conn.CreateCommand();
            volCmd.Transaction = tx;
            volCmd.CommandText = $"UPDATE volumes SET status = 'lost' WHERE id IN ({vp})";
            for (int i = 0; i < volumeIds.Count; i++) volCmd.Parameters.AddWithValue($"$v{i}", volumeIds[i]);
            volumeCount = volCmd.ExecuteNonQuery();

            // Collect (dat_line_id → derived_artifact_id) pairs for DatLineStore propagation
            using var getArtCmd = conn.CreateCommand();
            getArtCmd.Transaction = tx;
            getArtCmd.CommandText = $"""
                SELECT DISTINCT dat_line_id, derived_artifact_id
                FROM volume_artifacts
                WHERE volume_id IN ({vp})
                """;
            for (int i = 0; i < volumeIds.Count; i++) getArtCmd.Parameters.AddWithValue($"$v{i}", volumeIds[i]);
            using (var ra = getArtCmd.ExecuteReader())
            {
                while (ra.Read())
                {
                    var dl = ra.GetString(0);
                    var da = ra.GetString(1);
                    if (!artifactWork.TryGetValue(dl, out var lst)) { lst = []; artifactWork[dl] = lst; }
                    lst.Add(da);
                }
            }

            // Mark all volume_artifacts on these volumes as lost
            using var vaCmd = conn.CreateCommand();
            vaCmd.Transaction = tx;
            vaCmd.CommandText = $"UPDATE volume_artifacts SET status = 'lost' WHERE volume_id IN ({vp})";
            for (int i = 0; i < volumeIds.Count; i++) vaCmd.Parameters.AddWithValue($"$v{i}", volumeIds[i]);
            vaCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return (volumeCount, artifactWork);
    }

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
            INSERT INTO disks(id, label, status, declared_capacity_bytes, filesystem, brand, model, serial, created_at, updated_at, family)
            VALUES($id, $label, $status, $cap, $fs, $brand, $model, $serial, $created, $updated, $family)
            ON CONFLICT(id) DO UPDATE SET
                label                   = excluded.label,
                status                  = excluded.status,
                declared_capacity_bytes = excluded.declared_capacity_bytes,
                filesystem              = excluded.filesystem,
                brand                   = excluded.brand,
                model                   = excluded.model,
                serial                  = excluded.serial,
                family                  = excluded.family,
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
        cmd.Parameters.AddWithValue("$family",  d.Family);
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

    /// <summary>
    /// Returns the sum of planned_size_bytes for all volumes whose current location
    /// is on <paramref name="diskId"/>, excluding <paramref name="excludeVolumeId"/>.
    /// Used by the Resize Volume validation to calculate allocatable capacity.
    /// </summary>
    public long GetDiskPlannedUsageExcluding(string diskId, string excludeVolumeId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(v.planned_size_bytes), 0)
            FROM volumes v
            JOIN volume_locations vl ON vl.volume_id = v.id
            WHERE vl.disk_id = $diskId AND vl.is_current = 1
              AND v.id != $excludeId
            """;
        cmd.Parameters.AddWithValue("$diskId",    diskId);
        cmd.Parameters.AddWithValue("$excludeId", excludeVolumeId);
        return (long)(cmd.ExecuteScalar() ?? 0L);
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
        Family                = r.IsDBNull(10) ? "core" : r.GetString(10),
    };

    // ── Dashboard aggregates ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the number of volume_artifacts rows with status = 'present_in_final'.
    /// Used as a fast single-query count of physically stored artifacts for the dashboard.
    /// </summary>
    public int CountStoredArtifacts()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM volume_artifacts WHERE status = 'present_in_final'";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : Convert.ToInt32(result);
    }

    /// <summary>
    /// Returns the total number of artifacts assigned to each volume, keyed by volume id.
    /// Counts all rows in volume_artifacts regardless of status.
    /// </summary>
    public Dictionary<string, int> GetArtifactCountsByVolume()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT volume_id, COUNT(*)
            FROM volume_artifacts
            GROUP BY volume_id
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = (int)r.GetInt64(1);
        return result;
    }

    /// <summary>
    /// Returns the number of volumes currently located on each disk, keyed by disk id.
    /// Only counts locations where is_current = 1.
    /// </summary>
    public Dictionary<string, int> GetVolumeCountsByDisk()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT disk_id, COUNT(DISTINCT volume_id)
            FROM volume_locations
            WHERE is_current = 1 AND disk_id IS NOT NULL
            GROUP BY disk_id
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = (int)r.GetInt64(1);
        return result;
    }

    // ── Volumes ───────────────────────────────────────────────────────────────

    public List<VolumeRecord> GetVolumes()
    {
        var list = new List<VolumeRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, platform_id, dat_line_id, status, planned_size_bytes, actual_size_bytes, created_at, verified_at, health
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
                   v.planned_size_bytes, v.actual_size_bytes, v.created_at, v.verified_at, v.health
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
            INSERT INTO volumes(id, label, platform_id, dat_line_id, status, planned_size_bytes, actual_size_bytes, created_at, verified_at, health)
            VALUES($id, $label, $platId, $dlId, $status, $planned, $actual, $created, $verified, $health)
            ON CONFLICT(id) DO UPDATE SET
                label              = excluded.label,
                status             = excluded.status,
                planned_size_bytes = excluded.planned_size_bytes,
                actual_size_bytes  = excluded.actual_size_bytes,
                verified_at        = excluded.verified_at,
                health             = excluded.health
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
        cmd.Parameters.AddWithValue("$health",   v.Health);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates only the health column for the given volume.
    /// Allowed values: "ok" | "crit". Does not touch volume.status.
    /// </summary>
    public void UpdateVolumeHealth(string volumeId, string health)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE volumes SET health = $health WHERE id = $id";
        cmd.Parameters.AddWithValue("$health", health);
        cmd.Parameters.AddWithValue("$id",     volumeId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates only the status column for the given volume.
    /// Allowed values: "present" | "lost". Does not touch volume.health.
    /// </summary>
    public void UpdateVolumeStatus(string volumeId, string status)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE volumes SET status = $status WHERE id = $id";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$id",     volumeId);
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
            SELECT id, volume_id, dat_line_id, derived_artifact_id, content_identity_key, status, added_at_utc
            FROM volume_artifacts
            WHERE volume_id = $vid
            ORDER BY added_at_utc
            """;
        cmd.Parameters.AddWithValue("$vid", volumeId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new VolumeArtifactRecord
            {
                Id                 = r.GetString(0),
                VolumeId           = r.GetString(1),
                DatLineId          = r.GetString(2),
                DerivedArtifactId  = r.GetString(3),
                ContentIdentityKey = r.GetString(4),
                Status             = r.GetString(5),
                AddedAtUtc         = DateTime.Parse(r.GetString(6)),
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
            INSERT INTO volume_artifacts(id, volume_id, dat_line_id, derived_artifact_id, content_identity_key, status, added_at_utc)
            VALUES($id, $vid, $dlid, $daid, $cik, $status, $added)
            ON CONFLICT(volume_id, derived_artifact_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$id",     va.Id);
        cmd.Parameters.AddWithValue("$vid",    va.VolumeId);
        cmd.Parameters.AddWithValue("$dlid",   va.DatLineId);
        cmd.Parameters.AddWithValue("$daid",   va.DerivedArtifactId);
        cmd.Parameters.AddWithValue("$cik",    va.ContentIdentityKey);
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
            INSERT INTO volume_artifacts(id, volume_id, dat_line_id, derived_artifact_id, content_identity_key, status, added_at_utc)
            VALUES($id, $vid, $dlid, $daid, $cik, $status, $added)
            ON CONFLICT(volume_id, derived_artifact_id) DO NOTHING
            """;
        var pId     = cmd.Parameters.Add("$id",     Microsoft.Data.Sqlite.SqliteType.Text);
        var pVid    = cmd.Parameters.Add("$vid",    Microsoft.Data.Sqlite.SqliteType.Text);
        var pDlid   = cmd.Parameters.Add("$dlid",   Microsoft.Data.Sqlite.SqliteType.Text);
        var pDaid   = cmd.Parameters.Add("$daid",   Microsoft.Data.Sqlite.SqliteType.Text);
        var pCik    = cmd.Parameters.Add("$cik",    Microsoft.Data.Sqlite.SqliteType.Text);
        var pStatus = cmd.Parameters.Add("$status", Microsoft.Data.Sqlite.SqliteType.Text);
        var pAdded  = cmd.Parameters.Add("$added",  Microsoft.Data.Sqlite.SqliteType.Text);

        foreach (var va in batch)
        {
            pId.Value     = va.Id;
            pVid.Value    = va.VolumeId;
            pDlid.Value   = va.DatLineId;
            pDaid.Value   = va.DerivedArtifactId;
            pCik.Value    = va.ContentIdentityKey;
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

    /// <summary>
    /// For each derived artifact ID in <paramref name="derivedIds"/>, returns the
    /// distinct (Volume, DiskId, LocationType) tuples describing where those artifacts
    /// are currently stored. LocationType is one of "disk", "workspace", or "source".
    /// </summary>
    public List<(VolumeRecord Volume, string? DiskId, string LocationType)> GetVolumeStorageForDerivedIds(
        System.Collections.Generic.IReadOnlyList<string> derivedIds)
    {
        var result = new List<(VolumeRecord, string?, string)>();
        if (derivedIds.Count == 0) return result;

        using var conn = Open();
        using var cmd  = conn.CreateCommand();

        var placeholders = string.Join(",", System.Linq.Enumerable.Range(0, derivedIds.Count).Select(i => $"$id{i}"));
        cmd.CommandText = $"""
            SELECT DISTINCT
                v.id, v.label, v.platform_id, v.dat_line_id, v.status,
                v.planned_size_bytes, v.actual_size_bytes, v.created_at, v.verified_at, v.health,
                vl.disk_id, COALESCE(vl.location_type, 'unknown')
            FROM volume_artifacts va
            JOIN volumes v ON v.id = va.volume_id
            LEFT JOIN volume_locations vl ON vl.volume_id = v.id AND vl.is_current = 1
            WHERE va.derived_artifact_id IN ({placeholders})
            ORDER BY v.label
            """;
        for (int i = 0; i < derivedIds.Count; i++)
            cmd.Parameters.AddWithValue($"$id{i}", derivedIds[i]);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var vol    = ReadVolume(r);
            var diskId = r.IsDBNull(10) ? null : r.GetString(10);
            var loc    = r.GetString(11);
            result.Add((vol, diskId, loc));
        }
        return result;
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
        Health           = r.IsDBNull(9) ? "ok" : r.GetString(9),
    };

    /// <summary>
    /// Returns the derived artifact IDs that are assigned exclusively to
    /// <paramref name="volumeId"/> — i.e. not present on any other volume whose
    /// status is not "lost". Results are grouped by dat_line_id so callers can
    /// open the right per-DAT database for each group.
    /// </summary>
    public Dictionary<string, List<string>> GetDerivedArtifactsExclusiveToVolume(string volumeId)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT va.dat_line_id, va.derived_artifact_id
            FROM volume_artifacts va
            WHERE va.volume_id = $vid
              AND NOT EXISTS (
                  SELECT 1
                  FROM volume_artifacts va2
                  JOIN volumes v ON v.id = va2.volume_id
                  WHERE va2.derived_artifact_id = va.derived_artifact_id
                    AND va2.volume_id != $vid
                    AND v.status != 'lost'
              )
            """;
        cmd.Parameters.AddWithValue("$vid", volumeId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var dlId = r.GetString(0);
            var daId = r.GetString(1);
            if (!result.TryGetValue(dlId, out var list))
                result[dlId] = list = [];
            list.Add(daId);
        }
        return result;
    }

    // ── Integrity Validation ──────────────────────────────────────────────────

    /// <summary>Returns every row in volume_artifacts across all volumes.</summary>
    public List<VolumeArtifactRecord> GetAllVolumeArtifacts()
    {
        var list = new List<VolumeArtifactRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, volume_id, dat_line_id, derived_artifact_id, content_identity_key, status, added_at_utc
            FROM volume_artifacts
            ORDER BY volume_id, added_at_utc
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new VolumeArtifactRecord
            {
                Id                 = r.GetString(0),
                VolumeId           = r.GetString(1),
                DatLineId          = r.GetString(2),
                DerivedArtifactId  = r.GetString(3),
                ContentIdentityKey = r.GetString(4),
                Status             = r.GetString(5),
                AddedAtUtc         = DateTime.Parse(r.GetString(6)),
            });
        return list;
    }

    /// <summary>
    /// Returns (volume_id, derived_artifact_id) pairs from volume_artifacts where
    /// the volume_id does not exist in the volumes table. Used by integrity validation (Check 4a).
    /// </summary>
    public List<(string VolumeId, string DerivedArtifactId)> GetOrphanVolumeArtifactsByVolumeId()
    {
        var result = new List<(string, string)>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT va.volume_id, va.derived_artifact_id
            FROM volume_artifacts va
            LEFT JOIN volumes v ON v.id = va.volume_id
            WHERE v.id IS NULL
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((r.GetString(0), r.GetString(1)));
        return result;
    }

    // ── Volume Deletion ───────────────────────────────────────────────────────

    /// <summary>
    /// Removes only the specified derived artifact mappings from a volume's assignment table.
    /// Used after a partial reabsorb to keep the catalog consistent with what physically
    /// remains on the volume.
    /// </summary>
    public void RemoveVolumeArtifacts(string volumeId, IList<string> daIds)
    {
        if (daIds.Count == 0) return;
        using var conn = Open();
        using var tx   = conn.BeginTransaction();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM volume_artifacts WHERE volume_id = $vid AND derived_artifact_id = $did";
        var pVid = cmd.Parameters.Add("$vid", Microsoft.Data.Sqlite.SqliteType.Text);
        var pDid = cmd.Parameters.Add("$did", Microsoft.Data.Sqlite.SqliteType.Text);
        pVid.Value = volumeId;
        foreach (var daId in daIds)
        {
            pDid.Value = daId;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Removes all data associated with a volume: volume_artifacts, volume_locations,
    /// and the volume record itself, in a single atomic transaction.
    /// </summary>
    public void DeleteVolume(string volumeId)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();
        using var cmd  = conn.CreateCommand();

        cmd.CommandText = "DELETE FROM volume_artifacts WHERE volume_id = $vid";
        cmd.Parameters.AddWithValue("$vid", volumeId);
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DELETE FROM volume_locations WHERE volume_id = $vid";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$vid", volumeId);
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DELETE FROM volumes WHERE id = $vid";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$vid", volumeId);
        cmd.ExecuteNonQuery();

        tx.Commit();
    }

    // ── Disk Deletion ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if any volume whose current location is this disk is not yet marked lost.
    /// Used to gate Delete Disk — the disk may only be removed once all such volumes are lost.
    /// </summary>
    public bool HasActiveDiskVolumes(string diskId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM volume_locations vl
            JOIN volumes v ON v.id = vl.volume_id
            WHERE vl.disk_id = $diskId AND vl.is_current = 1
              AND v.status != 'lost'
            """;
        cmd.Parameters.AddWithValue("$diskId", diskId);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    /// <summary>
    /// Removes all volume_locations rows referencing this disk, then the disk record itself.
    /// Volumes and their artifacts are intentionally left untouched.
    /// </summary>
    public void DeleteDisk(string diskId)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();
        using var cmd  = conn.CreateCommand();

        cmd.CommandText = "DELETE FROM volume_locations WHERE disk_id = $id";
        cmd.Parameters.AddWithValue("$id", diskId);
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DELETE FROM disks WHERE id = $id";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$id", diskId);
        cmd.ExecuteNonQuery();

        tx.Commit();
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
