using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data.Identifiers;
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

            CREATE TABLE IF NOT EXISTS ecosystems (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                is_seeded  INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS hardware_families (
                id                  TEXT PRIMARY KEY,
                name                TEXT NOT NULL,
                manufacturer        TEXT NOT NULL,
                ecosystem_id        TEXT REFERENCES ecosystems(id),
                hardware_type_id    TEXT REFERENCES hardware_types(id),
                year_of_release     TEXT,
                media               TEXT,
                notes               TEXT,
                cpu                 TEXT,
                memory              TEXT,
                graphics            TEXT,
                sound               TEXT,
                display_resolution  TEXT,
                aspect_ratio        TEXT,
                scrape_system_id    TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS storage_strategies (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                description TEXT,
                sort_order  INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS media_types (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                is_seeded  INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS content_categories (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                is_seeded  INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS dat_lines (
                id                       TEXT PRIMARY KEY,
                hardware_family_id       TEXT NOT NULL,
                name                     TEXT NOT NULL,
                authority                TEXT NOT NULL,
                media_type_id            TEXT NOT NULL DEFAULT 'other' REFERENCES media_types(id),
                version                  TEXT,
                storage_strategy_id      TEXT,
                data_store_path          TEXT NOT NULL DEFAULT '',
                release_count            INTEGER NOT NULL DEFAULT 0,
                imported_at_utc          TEXT NOT NULL,
                transform_strategy_type  TEXT NOT NULL DEFAULT 'none',
                folder_transform_id      TEXT,
                file_handling            TEXT NOT NULL DEFAULT 'archives_pre_extraction',
                catalog_enabled          INTEGER NOT NULL DEFAULT 1,
                library_title_mode       TEXT NOT NULL DEFAULT 'dat',
                FOREIGN KEY(hardware_family_id) REFERENCES hardware_families(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_dat_lines_hardware_family_id ON dat_lines(hardware_family_id);

            -- Group DAT (additive, Phase 2). A dat_group is a super-unit grouping many leaf
            -- dat_lines. Single DAT = dat_lines.group_id IS NULL (see below). current_revision
            -- bootstraps at 0 and is advanced ONLY by a future finalizer, never by CRUD.
            -- id is COLLATE NOCASE so case-variant ids collide at the DB level (matches the
            -- case-insensitive DatGroupId policy). hardware_family_id is ON DELETE RESTRICT
            -- (non-destructive). Multiple groups may share a (family, authority) pair.
            CREATE TABLE IF NOT EXISTS dat_groups (
                id                  TEXT COLLATE NOCASE NOT NULL PRIMARY KEY,
                display_name        TEXT NOT NULL,
                hardware_family_id  TEXT NOT NULL REFERENCES hardware_families(id) ON DELETE RESTRICT,
                authority           TEXT NOT NULL,
                current_revision    INTEGER NOT NULL DEFAULT 0 CHECK (current_revision >= 0),
                created_at_utc      TEXT NOT NULL,
                updated_at_utc      TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_dat_groups_hardware_family_id ON dat_groups(hardware_family_id);
            CREATE INDEX IF NOT EXISTS idx_dat_groups_family_authority   ON dat_groups(hardware_family_id, authority);

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
                hardware_family_id  TEXT NOT NULL,
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
                transform_type    TEXT NOT NULL DEFAULT 'file_strategy',
                processor_type    TEXT NOT NULL DEFAULT 'file_oriented',
                output_kind       TEXT NOT NULL DEFAULT 'file',
                archive_tier      TEXT NOT NULL DEFAULT 'A'
            );

            CREATE TABLE IF NOT EXISTS dat_line_extension_transforms (
                dat_line_id    TEXT NOT NULL,
                file_extension TEXT NOT NULL,
                transform_id   TEXT,
                is_discard     INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (dat_line_id, file_extension)
            );

            CREATE TABLE IF NOT EXISTS catalog_working_state (
                item_id       TEXT NOT NULL PRIMARY KEY,
                working_state TEXT NOT NULL DEFAULT 'unknown',
                working_note  TEXT,
                is_manual     INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS metadata_value_mappings (
                field          TEXT NOT NULL,
                match_value    TEXT NOT NULL,
                replacement    TEXT NOT NULL,
                enabled        INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (field, match_value)
            );

            CREATE TABLE IF NOT EXISTS cache_packages (
                id                INTEGER PRIMARY KEY,
                package_path      TEXT NOT NULL UNIQUE,
                provider          TEXT NOT NULL,
                cache_provider_id TEXT NOT NULL,
                system_id         TEXT NOT NULL,
                system_name       TEXT NOT NULL,
                game_count        INTEGER NOT NULL DEFAULT 0,
                built_at_utc      TEXT NOT NULL DEFAULT '',
                indexed_at_utc    TEXT NOT NULL DEFAULT '',
                manifest_json     TEXT NOT NULL DEFAULT '',
                status            TEXT NOT NULL DEFAULT 'indexed'
            );

            CREATE TABLE IF NOT EXISTS cache_package_games (
                id                INTEGER PRIMARY KEY,
                package_id        INTEGER NOT NULL REFERENCES cache_packages(id) ON DELETE CASCADE,
                provider_game_id  TEXT NOT NULL,
                system_id         TEXT NOT NULL,
                title             TEXT NOT NULL,
                has_payload       INTEGER NOT NULL DEFAULT 0,
                has_media         INTEGER NOT NULL DEFAULT 0,
                payload_zip_entry TEXT NOT NULL DEFAULT '',
                scraped_at_utc    TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_cpg_package_id    ON cache_package_games(package_id);
            CREATE INDEX IF NOT EXISTS idx_cpg_system_title  ON cache_package_games(system_id, title);
            CREATE INDEX IF NOT EXISTS idx_cpg_provider_game ON cache_package_games(provider_game_id);

            CREATE TABLE IF NOT EXISTS cache_package_media (
                id               INTEGER PRIMARY KEY,
                game_row_id      INTEGER NOT NULL REFERENCES cache_package_games(id) ON DELETE CASCADE,
                provider_game_id TEXT NOT NULL,
                media_type       TEXT NOT NULL,
                region           TEXT NOT NULL DEFAULT '',
                index_n          INTEGER NOT NULL DEFAULT 0,
                zip_entry        TEXT NOT NULL,
                file_ext         TEXT NOT NULL DEFAULT '',
                file_size        INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_cpm_game_type ON cache_package_media(game_row_id, media_type);
            CREATE INDEX IF NOT EXISTS idx_cpm_prov_type ON cache_package_media(provider_game_id, media_type);

            CREATE TABLE IF NOT EXISTS cache_package_search_terms (
                id              INTEGER PRIMARY KEY,
                package_game_id INTEGER NOT NULL REFERENCES cache_package_games(id) ON DELETE CASCADE,
                term            TEXT NOT NULL,
                term_type       TEXT NOT NULL,
                normalized_term TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_cpst_normalized_term ON cache_package_search_terms(normalized_term);
            CREATE INDEX IF NOT EXISTS idx_cpst_game_id         ON cache_package_search_terms(package_game_id);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_cpst_unique   ON cache_package_search_terms(package_game_id, normalized_term);
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
                    ('chdman',      'chdman',      'chdman.exe',      1),
                    ('7zip',        '7zip',        '7zip.exe',        1),
                    ('dolphintool', 'dolphintool', 'DolphinTool.exe', 1),
                    ('wudcompress', 'wudcompress', 'WudCompress.exe', 1);
                """;
            toolSeed.ExecuteNonQuery();
        }

        // ── Migrate 7zip executable name (7z.exe → 7zip.exe) ─────────────────
        using var toolMigrate = conn.CreateCommand();
        toolMigrate.CommandText = """
            UPDATE tools SET executable_name = '7zip.exe'
            WHERE tool_id = '7zip' AND executable_name = '7z.exe'
            """;
        toolMigrate.ExecuteNonQuery();

        // ── Seed transforms if empty ──────────────────────────────────────────
        using var txCheck = conn.CreateCommand();
        txCheck.CommandText = "SELECT COUNT(*) FROM transforms";
        if ((long)(txCheck.ExecuteScalar() ?? 0L) == 0)
        {
            using var txSeed = conn.CreateCommand();
            txSeed.CommandText = """
                INSERT INTO transforms(transform_id, name, tool_id, command_template, output_extension, is_enabled, transform_type, processor_type, output_kind, archive_tier) VALUES
                    ('no_compression',           'No Compression (File)',       null,          '',                                                        '',     1, 'file_strategy',   'file_oriented',   'file',   'A'),
                    ('no_compression_folder',    'No Compression (Folder)',     null,          '',                                                        '',     1, 'folder_strategy', 'folder_oriented', 'folder', 'A'),
                    ('chd_cd_compression',       'CHD CD/GD Compression',                     'chdman',      'createcd -i "{input}" -o "{output}"',                    '.chd', 1, 'file_strategy',   'file_oriented',   'file',   'B'),
                    ('chd_dvd_compression',      'CHD DVD Compression',                       'chdman',      'createdvd -i "{input}" -o "{output}"',                   '.chd', 1, 'file_strategy',   'file_oriented',   'file',   'B'),
                    ('chd_gd_compression',       'CHD GD Compression (legacy/manual)',        'chdman',      'createcd -i "{input}" -o "{output}"',                    '.chd', 0, 'file_strategy',   'file_oriented',   'file',   'B'),
                    ('chd_psp_compression',      'CHD Compression (PSP)',                     'chdman',      'createdvd -hs 2048 -c zstd -i "{input}" -o "{output}"', '.chd', 1, 'file_strategy',   'file_oriented',   'file',   'B'),
                    ('chd_dreamcast_compression','CHD Dreamcast Compression (legacy/manual)', 'chdman',      'createcd -c zstd -i "{input}" -o "{output}"',            '.chd', 0, 'file_strategy',   'file_oriented',   'file',   'B'),
                    ('zip_compression',          'ZIP Compression (Folder)',     '7zip',        'a -tzip "{output}" * -w"{input}"',                       '.zip', 1, 'folder_strategy', 'folder_oriented', 'file',   'A'),
                    ('zip_file_compression',     'ZIP Compression (File)',       '7zip',        'a -tzip "{output}" "{input}"',                           '.zip', 1, 'file_strategy',   'file_oriented',   'file',   'A'),
                    ('rvz_compression',          'RVZ Compression',             'dolphintool',  'convert -f rvz -c zstd -l 5 -i "{input}" -o "{output}"','.rvz', 1, 'file_strategy',   'file_oriented',   'file',   'B'),
                    ('wux_compression',          'WUX Compression',             'wudcompress',  '-i "{input}" -o "{output}"',                             '.wux', 1, 'file_strategy',   'file_oriented',   'file',   'B');
                """;
            txSeed.ExecuteNonQuery();
        }

        // ── Migrate CHD CD/GD display names on existing catalogs ─────────────
        // "CHD CD Compression" (chd_cd_compression) is clarified for GD-ROM/Dreamcast
        // releases; chd_gd_compression / chd_dreamcast_compression are superseded
        // duplicate-command rows, kept only so already-saved extension mappings
        // still resolve. Guarded by old name so a user's own rename via Manage
        // Transforms is never overwritten. transform_id (the persisted identity
        // and the only key ReleaseShapeTransformPlanner/extension mappings use)
        // is unchanged.
        using var txRename = conn.CreateCommand();
        txRename.CommandText = """
            UPDATE transforms SET name = 'CHD CD/GD Compression'
                WHERE transform_id = 'chd_cd_compression' AND name = 'CHD CD Compression';
            UPDATE transforms SET name = 'CHD GD Compression (legacy/manual)', is_enabled = 0
                WHERE transform_id = 'chd_gd_compression' AND name = 'CHD GD Compression';
            UPDATE transforms SET name = 'CHD Dreamcast Compression (legacy/manual)', is_enabled = 0
                WHERE transform_id = 'chd_dreamcast_compression' AND name = 'CHD Compression (Dreamcast)';
            """;
        txRename.ExecuteNonQuery();

        // ── Migrate dat_lines: archive output validation columns (M1b) ───────
        // Nullable/additive — existing rows keep NULL, which reads as "unknown"
        // (never defaulted to single_file_flat). Idempotent via TryAddColumn.
        TryAddColumn(conn, "dat_lines", "archive_output_form                   TEXT NULL");
        TryAddColumn(conn, "dat_lines", "archive_output_validation_state       TEXT NULL");
        TryAddColumn(conn, "dat_lines", "archive_output_structural_fingerprint TEXT NULL");
        TryAddColumn(conn, "dat_lines", "archive_output_exclusion_fingerprint  TEXT NULL");
        TryAddColumn(conn, "dat_lines", "archive_output_validated_at_utc       TEXT NULL");

        // ── Migrate dat_lines: Group DAT metadata (Phase 2) ──────────────────
        // Additive / nullable. Existing rows stay NULL = Single DAT — NO backfill, NO
        // implicit group, NO revision assigned. group_id is a non-destructive FK to
        // dat_groups (ON DELETE RESTRICT). These columns are only READ in Phase 2; the
        // fingerprint/path values are populated by later phases, never here. dat_groups is
        // created above (same EnsureSchema batch), so it exists before this FK column is added.
        TryAddColumn(conn, "dat_lines", "group_id                     TEXT NULL REFERENCES dat_groups(id) ON DELETE RESTRICT");
        TryAddColumn(conn, "dat_lines", "relative_dat_path            TEXT NULL");
        TryAddColumn(conn, "dat_lines", "source_dat_name              TEXT NULL");
        TryAddColumn(conn, "dat_lines", "source_dat_sha256            TEXT NULL");
        TryAddColumn(conn, "dat_lines", "semantic_fingerprint         TEXT NULL");
        TryAddColumn(conn, "dat_lines", "semantic_fingerprint_version INTEGER NULL CHECK (semantic_fingerprint_version IS NULL OR semantic_fingerprint_version > 0)");
        TryAddColumn(conn, "dat_lines", "last_seen_group_revision     INTEGER NULL CHECK (last_seen_group_revision IS NULL OR last_seen_group_revision >= 0)");

        using (var groupIdx = conn.CreateCommand())
        {
            groupIdx.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_dat_lines_group_id ON dat_lines(group_id)";
            groupIdx.ExecuteNonQuery();
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
                    ('pinball',  'Pinball',  45, 1),
                    ('other',    'Other',    99, 1);
                """;
            seed.ExecuteNonQuery();
        }

        // ── Seed ecosystems (INSERT OR IGNORE — safe to re-run) ──────────────
        using var ecoSeed = conn.CreateCommand();
        ecoSeed.CommandText = """
            INSERT OR IGNORE INTO ecosystems(id, name, sort_order, is_seeded) VALUES
                ('nintendo',  'Nintendo',  10, 1),
                ('sony',      'Sony',      20, 1),
                ('sega',      'Sega',      30, 1),
                ('microsoft', 'Microsoft', 40, 1),
                ('nec',       'NEC',       50, 1),
                ('snk',       'SNK',       60, 1),
                ('atari',     'Atari',     70, 1),
                ('pc',        'PC',        80, 1),
                ('other',     'Other',     99, 1);
            """;
        ecoSeed.ExecuteNonQuery();

        // ── Seed media_types (INSERT OR IGNORE — safe to re-run) ─────────────
        // Adds any missing official media type without ever rewriting an existing row (INSERT OR
        // IGNORE), so a legacy catalog gains new rows (e.g. tape/bluray) on next start.
        using var mtSeed = conn.CreateCommand();
        mtSeed.CommandText = """
            INSERT OR IGNORE INTO media_types(id, name, sort_order, is_seeded) VALUES
                ('rom',       'ROM',       10,  1),
                ('cartridge', 'Cartridge', 20,  1),
                ('tape',      'Tape',      30,  1),
                ('floppy',    'Floppy',    40,  1),
                ('cd',        'CD',        50,  1),
                ('dvd',       'DVD',       60,  1),
                ('bluray',    'Blu-ray',   70,  1),
                ('hdd',       'HDD',       80,  1),
                ('digital',   'Digital',   90,  1),
                ('other',     'Other',     100, 1);
            """;
        mtSeed.ExecuteNonQuery();

        // Realign the display order of the KNOWN official seeded media types only. INSERT OR IGNORE
        // never updates a pre-existing row, so a legacy catalog would keep its old sort_order — this
        // reasserts the canonical order. Scope guard: matches on the explicit official id AND
        // is_seeded = 1, touches sort_order ONLY (never id/name/is_seeded), and never a custom or
        // non-seeded media type. No dat_line is affected and no media_type_id is changed.
        using var mtOrder = conn.CreateCommand();
        mtOrder.CommandText = """
            UPDATE media_types SET sort_order = CASE id
                WHEN 'rom'       THEN 10
                WHEN 'cartridge' THEN 20
                WHEN 'tape'      THEN 30
                WHEN 'floppy'    THEN 40
                WHEN 'cd'        THEN 50
                WHEN 'dvd'       THEN 60
                WHEN 'bluray'    THEN 70
                WHEN 'hdd'       THEN 80
                WHEN 'digital'   THEN 90
                WHEN 'other'     THEN 100
            END
            WHERE is_seeded = 1
              AND id IN ('rom','cartridge','tape','floppy','cd','dvd','bluray','hdd','digital','other');
            """;
        mtOrder.ExecuteNonQuery();

        // ── Seed content_categories (INSERT OR IGNORE — safe to re-run) ──────
        using var ccSeed = conn.CreateCommand();
        ccSeed.CommandText = """
            INSERT OR IGNORE INTO content_categories(id, name, sort_order, is_seeded) VALUES
                ('games',    'Games',    10, 1),
                ('software', 'Software', 20, 1),
                ('bios',     'BIOS',     30, 1),
                ('firmware', 'Firmware', 40, 1),
                ('dlc',      'DLC',      50, 1),
                ('eshop',    'eShop',    60, 1),
                ('media',    'Media',    70, 1),
                ('other',    'Other',    99, 1);
            """;
        ccSeed.ExecuteNonQuery();

        // ── Seed authorities (INSERT OR IGNORE — safe to re-run) ─────────────
        using var authSeed = conn.CreateCommand();
        authSeed.CommandText = """
            INSERT OR IGNORE INTO authorities(id, name, is_seeded) VALUES
                ('redump',  'ReDump',        1),
                ('nointro', 'No-Intro',      1),
                ('tosec',   'TOSEC',         1),
                ('mame',    'MAME',          1),
                ('fbneo',   'FinalBurn Neo', 1),
                ('custom',  'Custom',        1);
            """;
        authSeed.ExecuteNonQuery();

        // ── Seed metadata_value_mappings (INSERT OR IGNORE — safe to re-run) ─
        using var mapSeed = conn.CreateCommand();
        mapSeed.CommandText = """
            INSERT OR IGNORE INTO metadata_value_mappings(field, match_value, replacement, enabled) VALUES
                -- region
                ('region', 'wor',    'World',         1),
                ('region', 'world',  'World',         1),
                ('region', 'eu',     'Europe',        1),
                ('region', 'eur',    'Europe',        1),
                ('region', 'europe', 'Europe',        1),
                ('region', 'us',     'USA',           1),
                ('region', 'usa',    'USA',           1),
                ('region', 'jp',     'Japan',         1),
                ('region', 'jap',    'Japan',         1),
                ('region', 'japan',  'Japan',         1),
                ('region', 'ss',     'ScreenScraper', 1),
                -- release_type
                ('release_type', 'fantranslation',  'Fan Translation', 1),
                ('release_type', 'fan-translation', 'Fan Translation', 1),
                ('release_type', 'homebrew',        'Homebrew',        1),
                ('release_type', 'demo',            'Demo',            1),
                ('release_type', 'prototype',       'Prototype',       1),
                ('release_type', 'proto',           'Prototype',       1),
                ('release_type', 'hack',            'Hack',            1),
                ('release_type', 'retail',          'Retail',          1);
            """;
        mapSeed.ExecuteNonQuery();
    }

    // ── Hardware Families ─────────────────────────────────────────────────────

    public List<HardwareFamilyRecord> LoadPlatforms() => GetHardwareFamilies();

    public List<HardwareFamilyRecord> GetHardwareFamilies()
    {
        var list = new List<HardwareFamilyRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.name, p.manufacturer, p.ecosystem_id, p.hardware_type_id,
                   p.year_of_release, p.media, p.notes,
                   p.cpu, p.memory, p.graphics, p.sound,
                   p.display_resolution, p.aspect_ratio, p.scrape_system_id
            FROM hardware_families p
            LEFT JOIN hardware_types ht ON ht.id = p.hardware_type_id
            ORDER BY
                CASE WHEN ht.sort_order IS NULL THEN 9999 ELSE ht.sort_order END,
                p.manufacturer,
                p.name
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new HardwareFamilyRecord
            {
                Id                = reader.GetString(0),
                Name              = reader.GetString(1),
                Manufacturer      = reader.GetString(2),
                EcosystemId       = reader.IsDBNull(3)  ? "" : reader.GetString(3),
                HardwareTypeId    = reader.IsDBNull(4)  ? "" : reader.GetString(4),
                YearOfRelease     = reader.IsDBNull(5)  ? "" : reader.GetString(5),
                Media             = reader.IsDBNull(6)  ? "" : reader.GetString(6),
                Notes             = reader.IsDBNull(7)  ? "" : reader.GetString(7),
                Cpu               = reader.IsDBNull(8)  ? "" : reader.GetString(8),
                Memory            = reader.IsDBNull(9)  ? "" : reader.GetString(9),
                Graphics          = reader.IsDBNull(10) ? "" : reader.GetString(10),
                Sound             = reader.IsDBNull(11) ? "" : reader.GetString(11),
                DisplayResolution = reader.IsDBNull(12) ? "" : reader.GetString(12),
                AspectRatio       = reader.IsDBNull(13) ? "" : reader.GetString(13),
                ScrapeSystemId    = reader.IsDBNull(14) ? "" : reader.GetString(14),
            });
        return list;
    }

    public void SavePlatforms(List<HardwareFamilyRecord> records) => SaveHardwareFamilies(records);

    public void SaveHardwareFamilies(List<HardwareFamilyRecord> records)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();
        foreach (var p in records)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO hardware_families(
                    id, name, manufacturer, ecosystem_id, hardware_type_id, year_of_release, media, notes,
                    cpu, memory, graphics, sound, display_resolution, aspect_ratio, scrape_system_id)
                VALUES(
                    $id, $name, $manufacturer, $ecosystemId, $hardwareTypeId, $yearOfRelease, $media, $notes,
                    $cpu, $memory, $graphics, $sound, $displayResolution, $aspectRatio, $scrapeSystemId)
                ON CONFLICT(id) DO UPDATE SET
                    name               = excluded.name,
                    manufacturer       = excluded.manufacturer,
                    ecosystem_id       = excluded.ecosystem_id,
                    hardware_type_id   = excluded.hardware_type_id,
                    year_of_release    = excluded.year_of_release,
                    media              = excluded.media,
                    notes              = excluded.notes,
                    cpu                = excluded.cpu,
                    memory             = excluded.memory,
                    graphics           = excluded.graphics,
                    sound              = excluded.sound,
                    display_resolution = excluded.display_resolution,
                    aspect_ratio       = excluded.aspect_ratio,
                    scrape_system_id   = excluded.scrape_system_id
                """;
            cmd.Parameters.AddWithValue("$id",                p.Id);
            cmd.Parameters.AddWithValue("$name",              p.Name);
            cmd.Parameters.AddWithValue("$manufacturer",      p.Manufacturer);
            cmd.Parameters.AddWithValue("$ecosystemId",       NullIfEmpty(p.EcosystemId));
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
            cmd.Parameters.AddWithValue("$scrapeSystemId",    p.ScrapeSystemId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SaveHardwareFamily(HardwareFamilyRecord p)
        => SaveHardwareFamilies([p]);

    public HardwareFamilyRecord? GetPlatform(string id) => GetHardwareFamily(id);

    public HardwareFamilyRecord? GetHardwareFamily(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, manufacturer, ecosystem_id, hardware_type_id,
                   year_of_release, media, notes,
                   cpu, memory, graphics, sound, display_resolution, aspect_ratio, scrape_system_id
            FROM hardware_families WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new HardwareFamilyRecord
        {
            Id                = reader.GetString(0),
            Name              = reader.GetString(1),
            Manufacturer      = reader.GetString(2),
            EcosystemId       = reader.IsDBNull(3)  ? "" : reader.GetString(3),
            HardwareTypeId    = reader.IsDBNull(4)  ? "" : reader.GetString(4),
            YearOfRelease     = reader.IsDBNull(5)  ? "" : reader.GetString(5),
            Media             = reader.IsDBNull(6)  ? "" : reader.GetString(6),
            Notes             = reader.IsDBNull(7)  ? "" : reader.GetString(7),
            Cpu               = reader.IsDBNull(8)  ? "" : reader.GetString(8),
            Memory            = reader.IsDBNull(9)  ? "" : reader.GetString(9),
            Graphics          = reader.IsDBNull(10) ? "" : reader.GetString(10),
            Sound             = reader.IsDBNull(11) ? "" : reader.GetString(11),
            DisplayResolution = reader.IsDBNull(12) ? "" : reader.GetString(12),
            AspectRatio       = reader.IsDBNull(13) ? "" : reader.GetString(13),
            ScrapeSystemId    = reader.IsDBNull(14) ? "" : reader.GetString(14),
        };
    }

    public void DeleteHardwareFamily(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM hardware_families WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public bool HardwareFamilyHasDependencies(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dat_lines WHERE hardware_family_id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    // ── Ecosystems ────────────────────────────────────────────────────────────

    public List<EcosystemRecord> GetEcosystems()
    {
        var list = new List<EcosystemRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, sort_order, is_seeded FROM ecosystems ORDER BY sort_order, name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EcosystemRecord
            {
                Id        = r.GetString(0),
                Name      = r.GetString(1),
                SortOrder = r.GetInt32(2),
                IsSeeded  = r.GetInt32(3) != 0,
            });
        return list;
    }

    // ── Media Types ───────────────────────────────────────────────────────────

    public List<MediaTypeRecord> GetMediaTypes()
    {
        var list = new List<MediaTypeRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, sort_order, is_seeded FROM media_types ORDER BY sort_order, name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new MediaTypeRecord
            {
                Id        = r.GetString(0),
                Name      = r.GetString(1),
                SortOrder = r.GetInt32(2),
                IsSeeded  = r.GetInt32(3) != 0,
            });
        return list;
    }

    // ── Content Categories ────────────────────────────────────────────────────

    public List<ContentCategoryRecord> GetContentCategories()
    {
        var list = new List<ContentCategoryRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, sort_order, is_seeded FROM content_categories ORDER BY sort_order, name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ContentCategoryRecord
            {
                Id        = r.GetString(0),
                Name      = r.GetString(1),
                SortOrder = r.GetInt32(2),
                IsSeeded  = r.GetInt32(3) != 0,
            });
        return list;
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
        cmd.CommandText = "SELECT 1 FROM hardware_families WHERE hardware_type_id = $id LIMIT 1";
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
            SELECT transform_id, name, tool_id, command_template, output_extension, is_enabled,
                   transform_type, processor_type, output_kind, archive_tier
            FROM transforms
            ORDER BY transform_id
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var transformType = r.IsDBNull(6) ? "file_strategy" : r.GetString(6);
            var processorType = r.IsDBNull(7)
                ? (transformType == "folder_strategy" ? "folder_oriented" : "file_oriented")
                : r.GetString(7);
            list.Add(new TransformRecord
            {
                Id              = r.GetString(0),
                Name            = r.GetString(1),
                ToolId          = r.IsDBNull(2) ? "" : r.GetString(2),
                CommandTemplate = r.GetString(3),
                OutputExtension = r.GetString(4),
                IsEnabled       = r.GetInt32(5) != 0,
                TransformType   = transformType,
                ProcessorType   = processorType,
                OutputKind      = r.IsDBNull(8) ? "file" : r.GetString(8),
                ArchiveTier     = r.IsDBNull(9) ? "A"    : r.GetString(9),
            });
        }
        return list;
    }

    public void SaveTransform(TransformRecord t)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transforms(transform_id, name, tool_id, command_template, output_extension, is_enabled, transform_type, processor_type, output_kind, archive_tier)
            VALUES($id, $name, $toolId, $cmd, $ext, $enabled, $type, $processorType, $outputKind, $tier)
            ON CONFLICT(transform_id) DO UPDATE SET
                name              = excluded.name,
                tool_id           = excluded.tool_id,
                command_template  = excluded.command_template,
                output_extension  = excluded.output_extension,
                is_enabled        = excluded.is_enabled,
                transform_type    = excluded.transform_type,
                processor_type    = excluded.processor_type,
                output_kind       = excluded.output_kind,
                archive_tier      = excluded.archive_tier
            """;
        cmd.Parameters.AddWithValue("$id",            t.Id);
        cmd.Parameters.AddWithValue("$name",          t.Name);
        cmd.Parameters.AddWithValue("$toolId",        t.ToolId.Length > 0 ? t.ToolId : DBNull.Value);
        cmd.Parameters.AddWithValue("$cmd",           t.CommandTemplate);
        cmd.Parameters.AddWithValue("$ext",           t.OutputExtension);
        cmd.Parameters.AddWithValue("$enabled",       t.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$type",          t.TransformType);
        cmd.Parameters.AddWithValue("$processorType", t.ProcessorType);
        cmd.Parameters.AddWithValue("$outputKind",    t.OutputKind);
        cmd.Parameters.AddWithValue("$tier",          t.ArchiveTier.Length > 0 ? t.ArchiveTier : "A");
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
            SELECT id, hardware_family_id, name, authority, media_type_id, version, storage_strategy_id, data_store_path, release_count, imported_at_utc,
                   transform_strategy_type, folder_transform_id, file_handling, catalog_enabled, library_title_mode
            FROM dat_lines
            ORDER BY imported_at_utc DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new DatLineRecord
            {
                Id                    = reader.GetString(0),
                HardwareFamilyId      = reader.GetString(1),
                Name                  = reader.GetString(2),
                Authority             = reader.GetString(3),
                MediaTypeId           = reader.IsDBNull(4) ? "other" : reader.GetString(4),
                Version               = reader.IsDBNull(5) ? "" : reader.GetString(5),
                StorageStrategyId     = reader.IsDBNull(6) ? "" : reader.GetString(6),
                DataStorePath         = reader.IsDBNull(7) ? "" : reader.GetString(7),
                ReleaseCount          = reader.GetInt32(8),
                ImportedAtUtc         = DateTime.Parse(reader.GetString(9)),
                TransformStrategyType = reader.IsDBNull(10) ? "none" : reader.GetString(10),
                FolderTransformId     = reader.IsDBNull(11) ? "" : reader.GetString(11),
                FileHandling          = reader.GetString(12),
                CatalogEnabled        = reader.IsDBNull(13) || reader.GetInt32(13) != 0,
                LibraryTitleMode      = reader.IsDBNull(14) ? "dat" : reader.GetString(14),
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
                INSERT INTO dat_lines(id, hardware_family_id, name, authority, media_type_id, version, storage_strategy_id, data_store_path, release_count, imported_at_utc, catalog_enabled, library_title_mode)
                VALUES($id, $hardwareFamilyId, $name, $authority, $mediaTypeId, $version, $storageStrategyId, $dataStorePath, $releaseCount, $importedAt, $catalogEnabled, $libraryTitleMode)
                ON CONFLICT(id) DO UPDATE SET
                    name                 = excluded.name,
                    authority            = excluded.authority,
                    media_type_id        = excluded.media_type_id,
                    version              = excluded.version,
                    storage_strategy_id  = excluded.storage_strategy_id,
                    data_store_path      = excluded.data_store_path,
                    release_count        = excluded.release_count,
                    imported_at_utc      = excluded.imported_at_utc,
                    catalog_enabled      = excluded.catalog_enabled,
                    library_title_mode   = excluded.library_title_mode
                """;
            cmd.Parameters.AddWithValue("$id",                dl.Id);
            cmd.Parameters.AddWithValue("$hardwareFamilyId",  dl.HardwareFamilyId);
            cmd.Parameters.AddWithValue("$name",              dl.Name);
            cmd.Parameters.AddWithValue("$authority",         dl.Authority);
            cmd.Parameters.AddWithValue("$mediaTypeId",       dl.MediaTypeId.Length > 0 ? dl.MediaTypeId : "other");
            cmd.Parameters.AddWithValue("$version",           dl.Version);
            cmd.Parameters.AddWithValue("$storageStrategyId", NullIfEmpty(dl.StorageStrategyId));
            cmd.Parameters.AddWithValue("$dataStorePath",     dl.DataStorePath);
            cmd.Parameters.AddWithValue("$releaseCount",      dl.ReleaseCount);
            cmd.Parameters.AddWithValue("$importedAt",        dl.ImportedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$catalogEnabled",    dl.CatalogEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$libraryTitleMode",  dl.LibraryTitleMode);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Updates only the mutable metadata fields on an existing DAT line.
    /// Identity fields (hardware_family_id, authority, media_type_id, storage_strategy_id,
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

    // ── Group DAT (Phase 2) ────────────────────────────────────────────────────
    // Additive persistence for dat_groups. NO membership/assignment, NO revision
    // advancement, NO delete API — those belong to later phases. current_revision is
    // fixed to 0 at creation and only a future finalizer may advance it.

    /// <summary>
    /// Creates a new Group DAT (pure INSERT, never an upsert). The caller supplies an already
    /// valid <see cref="DatGroupId"/>; the id is NOT re-normalized. <c>current_revision</c> is
    /// forced to 0 and timestamps are generated internally. Throws
    /// <see cref="ArgumentException"/> for an invalid id / empty display name / unknown hardware
    /// family, and <see cref="InvalidOperationException"/> if the id already exists (including a
    /// case-variant, rejected by the NOCASE primary key).
    /// </summary>
    public DatGroupRecord CreateDatGroup(
        DatGroupId id, string displayName, string hardwareFamilyId, string authority)
    {
        if (!id.ConformsToNewPolicy)
            throw new ArgumentException("Group id does not satisfy the new-id policy.", nameof(id));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name must be non-empty.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(hardwareFamilyId))
            throw new ArgumentException("Hardware family id must be non-empty.", nameof(hardwareFamilyId));
        if (GetHardwareFamily(hardwareFamilyId) is null)
            throw new ArgumentException($"Hardware family '{hardwareFamilyId}' does not exist.", nameof(hardwareFamilyId));
        if (string.IsNullOrWhiteSpace(authority))
            throw new ArgumentException("Authority must be non-empty.", nameof(authority));

        var now = DateTime.UtcNow;
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        // FK enforcement is per-connection; enable it inline (repo precedent) so the
        // hardware_family_id FK is honoured even though Open() does not set the pragma.
        cmd.CommandText = """
            PRAGMA foreign_keys = ON;
            INSERT INTO dat_groups(id, display_name, hardware_family_id, authority, current_revision, created_at_utc, updated_at_utc)
            VALUES($id, $displayName, $hardwareFamilyId, $authority, 0, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$id",               id.Value);
        cmd.Parameters.AddWithValue("$displayName",      displayName);
        cmd.Parameters.AddWithValue("$hardwareFamilyId", hardwareFamilyId);
        cmd.Parameters.AddWithValue("$authority",        authority);
        cmd.Parameters.AddWithValue("$now",              now.ToString("o"));
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)   // SQLITE_CONSTRAINT
        {
            throw new InvalidOperationException(
                $"A Group DAT with id '{id.Value}' already exists (ids are case-insensitive).", ex);
        }

        return new DatGroupRecord
        {
            Id               = id,
            DisplayName      = displayName,
            HardwareFamilyId = hardwareFamilyId,
            Authority        = authority,
            CurrentRevision  = 0,
            CreatedAtUtc     = now,
            UpdatedAtUtc     = now,
        };
    }

    /// <summary>All Group DATs in a deterministic order (creation time, then id).</summary>
    public List<DatGroupRecord> LoadDatGroups()
    {
        var list = new List<DatGroupRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, display_name, hardware_family_id, authority, current_revision, created_at_utc, updated_at_utc
            FROM dat_groups
            ORDER BY created_at_utc, id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(MapDatGroup(reader));
        return list;
    }

    /// <summary>The Group DAT with this id, or null. Comparison is case-insensitive.</summary>
    public DatGroupRecord? GetDatGroup(DatGroupId id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, display_name, hardware_family_id, authority, current_revision, created_at_utc, updated_at_utc
            FROM dat_groups
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id.Value ?? "");
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapDatGroup(reader) : null;
    }

    /// <summary>True when a Group DAT with this id exists (case-insensitive).</summary>
    public bool DatGroupExists(DatGroupId id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dat_groups WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id.Value ?? "");
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Updates ONLY the display name (and <c>updated_at_utc</c>) of an existing Group DAT.
    /// Never touches id, hardware family, authority, or current_revision. Throws
    /// <see cref="ArgumentException"/> for an empty name and
    /// <see cref="InvalidOperationException"/> if the group does not exist.
    /// </summary>
    public void UpdateDatGroupDisplayName(DatGroupId id, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name must be non-empty.", nameof(displayName));

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dat_groups
            SET display_name   = $displayName,
                updated_at_utc = $now
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$displayName", displayName);
        cmd.Parameters.AddWithValue("$now",         DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id",          id.Value ?? "");
        var affected = cmd.ExecuteNonQuery();
        if (affected == 0)
            throw new InvalidOperationException($"No Group DAT with id '{id.Value}'.");
    }

    private static DatGroupRecord MapDatGroup(SqliteDataReader reader) => new()
    {
        Id               = DatGroupId.FromPersisted(reader.GetString(0)),
        DisplayName      = reader.GetString(1),
        HardwareFamilyId = reader.GetString(2),
        Authority        = reader.GetString(3),
        CurrentRevision  = reader.GetInt32(4),
        CreatedAtUtc     = DateTime.Parse(reader.GetString(5)),
        UpdatedAtUtc     = DateTime.Parse(reader.GetString(6)),
    };

    /// <summary>
    /// Reads the nullable Group DAT metadata columns for a leaf, or null if the leaf row does
    /// not exist. All fields are NULL for Single DAT / legacy leaves (Phase 2 only reads them).
    /// </summary>
    public DatLineGroupMetadataRecord? GetDatLineGroupMetadata(string datLineId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, group_id, relative_dat_path, source_dat_name, source_dat_sha256,
                   semantic_fingerprint, semantic_fingerprint_version, last_seen_group_revision
            FROM dat_lines
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", datLineId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new DatLineGroupMetadataRecord
        {
            DatLineId                  = reader.GetString(0),
            GroupId                    = reader.IsDBNull(1) ? null : reader.GetString(1),
            RelativeDatPath            = reader.IsDBNull(2) ? null : reader.GetString(2),
            SourceDatName              = reader.IsDBNull(3) ? null : reader.GetString(3),
            SourceDatSha256            = reader.IsDBNull(4) ? null : reader.GetString(4),
            SemanticFingerprint        = reader.IsDBNull(5) ? null : reader.GetString(5),
            SemanticFingerprintVersion = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            LastSeenGroupRevision      = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        };
    }

    // ── Group DAT: atomic Create (group + leaves + metadata + working states) ────

    // Test-only seams (internal, never set in production; no production behaviour depends on them).
    // The failure-injection ones are justified because there is no reliable way to force a
    // mid-transaction error after some inserts once the payload is validated.
    internal Action<int>?              OnLeafInsertedForTests;      // fired after each leaf insert (1-based index)
    internal Action?                   OnBeforeCommitForTests;      // fired just before Commit
    internal Action<SqliteConnection>? OnTransactionOpenedForTests; // fired inside the tx with the live connection

    /// <summary>
    /// Atomically registers a new Group DAT and all of its leaves in the catalog. Within a SINGLE
    /// connection and SINGLE transaction it: validates against the live catalog, inserts <c>dat_groups</c>
    /// (current_revision = 0), inserts every <c>dat_lines</c> row already complete with its Group metadata
    /// columns (no window without metadata), applies the initial working states (same <c>is_manual = 0</c>
    /// rule as import), and commits. Any failure — validation, SQLite constraint, or cancellation before
    /// commit — rolls back the whole transaction: no group, no leaf, no metadata, no working state remains.
    /// Performs NO filesystem access and does not verify that any leaf database physically exists (that is
    /// the caller's responsibility, before calling this). Additive: it does not use or affect
    /// <see cref="SaveDatLines"/> or any Single-DAT path.
    /// </summary>
    /// <exception cref="GroupDatCatalogValidationException">A deterministic validation failure.</exception>
    /// <exception cref="SqliteException">An unexpected database error (distinct from validation).</exception>
    public void CreateDatGroupWithLeaves(
        GroupDatCatalogCreateRequest request,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var conn = Open();

        // FK enforcement is per-connection and CANNOT change while a transaction is active — so enable
        // it BEFORE BeginTransaction(). This makes the dat_lines/dat_groups FKs a real DB-level defence
        // (not merely the application validations below).
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        OnTransactionOpenedForTests?.Invoke(conn);

        ValidateGroupAndLeaves(conn, tx, request);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow.ToString("o");

        // ── dat_groups ──
        using (var g = conn.CreateCommand())
        {
            g.Transaction = tx;
            g.CommandText = """
                INSERT INTO dat_groups(id, display_name, hardware_family_id, authority, current_revision, created_at_utc, updated_at_utc)
                VALUES($id, $displayName, $hardwareFamilyId, $authority, 0, $now, $now)
                """;
            g.Parameters.AddWithValue("$id",               request.GroupId);
            g.Parameters.AddWithValue("$displayName",      request.DisplayName);
            g.Parameters.AddWithValue("$hardwareFamilyId", request.HardwareFamilyId);
            g.Parameters.AddWithValue("$authority",        request.Authority);
            g.Parameters.AddWithValue("$now",              now);
            g.ExecuteNonQuery();
        }

        // ── dat_lines (each born complete with its Group metadata) ──
        int inserted = 0;
        foreach (var leaf in request.Leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InsertGroupLeaf(conn, tx, request.GroupId, leaf, now);
            OnLeafInsertedForTests?.Invoke(++inserted);
        }

        // ── initial working states (same rule as Single-DAT import) ──
        foreach (var leaf in request.Leaves)
            foreach (var ws in leaf.InitialWorkingStates)
                WriteWorkingStateIfNotManual(conn, tx, ws);

        OnBeforeCommitForTests?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        tx.Commit();
    }

    private void InsertGroupLeaf(
        SqliteConnection conn, SqliteTransaction tx, string groupId, GroupDatCatalogLeafCreate leaf, string now)
    {
        var dl = leaf.DatLine;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Same columns Single-DAT import writes (transform_strategy_type / folder_transform_id /
        // file_handling take their schema defaults) PLUS the Group metadata columns, in one row.
        cmd.CommandText = """
            INSERT INTO dat_lines(
                id, hardware_family_id, name, authority, media_type_id, version, storage_strategy_id,
                data_store_path, release_count, imported_at_utc, catalog_enabled, library_title_mode,
                group_id, relative_dat_path, source_dat_name, source_dat_sha256,
                semantic_fingerprint, semantic_fingerprint_version, last_seen_group_revision)
            VALUES(
                $id, $hardwareFamilyId, $name, $authority, $mediaTypeId, $version, $storageStrategyId,
                $dataStorePath, $releaseCount, $importedAt, $catalogEnabled, $libraryTitleMode,
                $groupId, $relativeDatPath, $sourceDatName, $sourceDatSha256,
                $semanticFingerprint, $semanticFingerprintVersion, $lastSeenGroupRevision)
            """;
        cmd.Parameters.AddWithValue("$id",                dl.Id);
        cmd.Parameters.AddWithValue("$hardwareFamilyId",  dl.HardwareFamilyId);
        cmd.Parameters.AddWithValue("$name",              dl.Name);
        cmd.Parameters.AddWithValue("$authority",         dl.Authority);
        cmd.Parameters.AddWithValue("$mediaTypeId",       dl.MediaTypeId.Length > 0 ? dl.MediaTypeId : "other");
        cmd.Parameters.AddWithValue("$version",           dl.Version);
        cmd.Parameters.AddWithValue("$storageStrategyId", NullIfEmpty(dl.StorageStrategyId));
        cmd.Parameters.AddWithValue("$dataStorePath",     dl.DataStorePath);
        cmd.Parameters.AddWithValue("$releaseCount",      dl.ReleaseCount);
        cmd.Parameters.AddWithValue("$importedAt",        dl.ImportedAtUtc == default ? now : dl.ImportedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$catalogEnabled",    dl.CatalogEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$libraryTitleMode",  dl.LibraryTitleMode);
        cmd.Parameters.AddWithValue("$groupId",           groupId);
        cmd.Parameters.AddWithValue("$relativeDatPath",   leaf.RelativeDatPath);
        cmd.Parameters.AddWithValue("$sourceDatName",     leaf.SourceDatName);
        cmd.Parameters.AddWithValue("$sourceDatSha256",   leaf.SourceDatSha256);
        cmd.Parameters.AddWithValue("$semanticFingerprint",        (object?)leaf.SemanticFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$semanticFingerprintVersion", (object?)leaf.SemanticFingerprintVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastSeenGroupRevision",      leaf.LastSeenGroupRevision);
        cmd.ExecuteNonQuery();
    }

    private static void WriteWorkingStateIfNotManual(
        SqliteConnection conn, SqliteTransaction tx, GroupDatInitialWorkingState ws)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO catalog_working_state(item_id, working_state, working_note, is_manual)
            VALUES($itemId, $state, $note, 0)
            ON CONFLICT(item_id) DO UPDATE SET
                working_state = excluded.working_state,
                working_note  = excluded.working_note
            WHERE is_manual = 0
            """;
        cmd.Parameters.AddWithValue("$itemId", ws.ItemId);
        cmd.Parameters.AddWithValue("$state",  ws.State);
        cmd.Parameters.AddWithValue("$note",   (object?)ws.Note ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ── Validation (all within the create transaction; no filesystem access) ─────

    private void ValidateGroupAndLeaves(
        SqliteConnection conn, SqliteTransaction tx, GroupDatCatalogCreateRequest request)
    {
        static void Fail(GroupDatCatalogCreateError e, string msg, string? leaf = null)
            => throw new GroupDatCatalogValidationException(e, msg, leaf);

        // ── Group ──
        if (!DatTechnicalIdPolicy.IsValidNew(request.GroupId))
            Fail(GroupDatCatalogCreateError.InvalidGroupId, $"Group id '{request.GroupId}' is not a valid new id.");
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            Fail(GroupDatCatalogCreateError.EmptyDisplayName, "Group display name must be non-empty.");
        if (string.IsNullOrWhiteSpace(request.Authority))
            Fail(GroupDatCatalogCreateError.InvalidAuthority, "Group authority must be non-empty.");
        if (RowExists(conn, tx, "SELECT 1 FROM dat_groups WHERE id = $v LIMIT 1", request.GroupId))
            Fail(GroupDatCatalogCreateError.GroupIdCollision, $"A Group DAT with id '{request.GroupId}' already exists (case-insensitive).");
        if (!RowExists(conn, tx, "SELECT 1 FROM hardware_families WHERE id = $v LIMIT 1", request.HardwareFamilyId))
            Fail(GroupDatCatalogCreateError.HardwareFamilyMissing, $"Hardware family '{request.HardwareFamilyId}' does not exist.");

        // ── Leaves ──
        if (request.Leaves is null || request.Leaves.Count == 0)
            Fail(GroupDatCatalogCreateError.NoLeaves, "A Group DAT must be created with at least one leaf.");

        var seenLeafIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var leaf in request.Leaves!)
        {
            var dl = leaf.DatLine ?? throw new GroupDatCatalogValidationException(
                GroupDatCatalogCreateError.InvalidLeafId, "Leaf dat_line record is null.");
            var id = dl.Id;

            if (!DatTechnicalIdPolicy.IsValidNew(id))
                Fail(GroupDatCatalogCreateError.InvalidLeafId, $"Leaf id '{id}' is not a valid new id.", id);
            if (!seenLeafIds.Add(id))
                Fail(GroupDatCatalogCreateError.DuplicateLeafIdInPayload, $"Leaf id '{id}' appears more than once in the request (case-insensitive).", id);
            if (RowExists(conn, tx, "SELECT 1 FROM dat_lines WHERE id = $v COLLATE NOCASE LIMIT 1", id))
                Fail(GroupDatCatalogCreateError.LeafIdCollision, $"A dat_line with id '{id}' already exists (case-insensitive).", id);
            if (!string.Equals(dl.HardwareFamilyId, request.HardwareFamilyId, StringComparison.Ordinal))
                Fail(GroupDatCatalogCreateError.LeafSystemMismatch, $"Leaf '{id}' belongs to system '{dl.HardwareFamilyId}', not the group's '{request.HardwareFamilyId}'.", id);
            if (!string.Equals(dl.Authority, request.Authority, StringComparison.Ordinal))
                Fail(GroupDatCatalogCreateError.LeafAuthorityMismatch, $"Leaf '{id}' has authority '{dl.Authority}', not the group's '{request.Authority}'.", id);
            var mediaType = dl.MediaTypeId.Length > 0 ? dl.MediaTypeId : "other";
            if (!RowExists(conn, tx, "SELECT 1 FROM media_types WHERE id = $v LIMIT 1", mediaType))
                Fail(GroupDatCatalogCreateError.MediaTypeMissing, $"Leaf '{id}' references unknown media type '{mediaType}'.", id);

            if (string.IsNullOrEmpty(dl.DataStorePath))
                Fail(GroupDatCatalogCreateError.EmptyDataStorePath, $"Leaf '{id}' has an empty data store path.", id);
            if (!seenPaths.Add(dl.DataStorePath))
                Fail(GroupDatCatalogCreateError.DuplicateDataStorePathInPayload, $"Data store path '{dl.DataStorePath}' appears more than once in the request (case-insensitive).", id);
            if (RowExists(conn, tx, "SELECT 1 FROM dat_lines WHERE data_store_path = $v COLLATE NOCASE LIMIT 1", dl.DataStorePath))
                Fail(GroupDatCatalogCreateError.DataStorePathCollision, $"Data store path '{dl.DataStorePath}' is already assigned to another dat_line.", id);

            if (!IsValidRelativeDatPath(leaf.RelativeDatPath))
                Fail(GroupDatCatalogCreateError.InvalidRelativeDatPath, $"Leaf '{id}' has an invalid relative DAT path '{leaf.RelativeDatPath}'.", id);
            if (string.IsNullOrWhiteSpace(leaf.SourceDatName))
                Fail(GroupDatCatalogCreateError.EmptySourceDatName, $"Leaf '{id}' has an empty source DAT name.", id);
            if (!IsValidSha256(leaf.SourceDatSha256))
                Fail(GroupDatCatalogCreateError.InvalidSourceSha256, $"Leaf '{id}' has a malformed source DAT SHA-256.", id);
            if (leaf.LastSeenGroupRevision != 0)
                Fail(GroupDatCatalogCreateError.InvalidLastSeenRevision, $"Leaf '{id}' must have last_seen_group_revision = 0 for Group Create v1.", id);

            var fpNull  = leaf.SemanticFingerprint is null;
            var verNull = leaf.SemanticFingerprintVersion is null;
            if (fpNull != verNull || (!fpNull && (string.IsNullOrWhiteSpace(leaf.SemanticFingerprint) || leaf.SemanticFingerprintVersion <= 0)))
                Fail(GroupDatCatalogCreateError.InvalidSemanticFingerprint, $"Leaf '{id}' has inconsistent semantic fingerprint / version.", id);
        }
    }

    private static bool RowExists(SqliteConnection conn, SqliteTransaction tx, string sql, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$v", value ?? "");
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>A relative, '/'-normalized DAT path with no root and no <c>..</c> traversal (no empty segments).</summary>
    private static bool IsValidRelativeDatPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        if (p.Contains('\\')) return false;              // must be forward-slash normalized
        if (p.StartsWith('/')) return false;             // not rooted
        if (Path.IsPathRooted(p)) return false;          // no drive/UNC root
        foreach (var seg in p.Split('/'))
        {
            if (seg.Length == 0) return false;           // no empty segment (leading/trailing/double slash)
            if (seg == "..") return false;               // no traversal
        }
        return true;
    }

    private static bool IsValidSha256(string? s)
        => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    /// <summary>
    /// All leaves of a Group DAT (dat_line row + Group metadata), in a single query with no N+1, ordered
    /// deterministically by relative DAT path then id. Case-insensitive on <c>group_id</c>. Read-only;
    /// returns an empty list for a group with no leaves. Does not modify anything.
    /// </summary>
    public List<GroupLeafRecord> GetLeavesForGroup(string groupId)
    {
        var result = new List<GroupLeafRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, hardware_family_id, name, authority, media_type_id, version, storage_strategy_id,
                   data_store_path, release_count, imported_at_utc, transform_strategy_type,
                   folder_transform_id, file_handling, catalog_enabled, library_title_mode,
                   group_id, relative_dat_path, source_dat_name, source_dat_sha256,
                   semantic_fingerprint, semantic_fingerprint_version, last_seen_group_revision
            FROM dat_lines
            WHERE group_id = $gid COLLATE NOCASE
            ORDER BY relative_dat_path, id
            """;
        cmd.Parameters.AddWithValue("$gid", groupId ?? "");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var datLine = new DatLineRecord
            {
                Id                    = r.GetString(0),
                HardwareFamilyId      = r.GetString(1),
                Name                  = r.GetString(2),
                Authority             = r.GetString(3),
                MediaTypeId           = r.IsDBNull(4) ? "other" : r.GetString(4),
                Version               = r.IsDBNull(5) ? "" : r.GetString(5),
                StorageStrategyId     = r.IsDBNull(6) ? "" : r.GetString(6),
                DataStorePath         = r.IsDBNull(7) ? "" : r.GetString(7),
                ReleaseCount          = r.GetInt32(8),
                ImportedAtUtc         = DateTime.Parse(r.GetString(9)),
                TransformStrategyType = r.IsDBNull(10) ? "none" : r.GetString(10),
                FolderTransformId     = r.IsDBNull(11) ? "" : r.GetString(11),
                FileHandling          = r.GetString(12),
                CatalogEnabled        = r.IsDBNull(13) || r.GetInt32(13) != 0,
                LibraryTitleMode      = r.IsDBNull(14) ? "dat" : r.GetString(14),
            };
            var meta = new DatLineGroupMetadataRecord
            {
                DatLineId                  = r.GetString(0),
                GroupId                    = r.IsDBNull(15) ? null : r.GetString(15),
                RelativeDatPath            = r.IsDBNull(16) ? null : r.GetString(16),
                SourceDatName              = r.IsDBNull(17) ? null : r.GetString(17),
                SourceDatSha256            = r.IsDBNull(18) ? null : r.GetString(18),
                SemanticFingerprint        = r.IsDBNull(19) ? null : r.GetString(19),
                SemanticFingerprintVersion = r.IsDBNull(20) ? null : r.GetInt32(20),
                LastSeenGroupRevision      = r.IsDBNull(21) ? null : r.GetInt32(21),
            };
            result.Add(new GroupLeafRecord(datLine, meta));
        }
        return result;
    }

    /// <summary>
    /// Atomically applies ONE uniform configuration (file handling + transform strategy + folder transform)
    /// to every leaf of a Group, in a single connection + single transaction (all-or-nothing). Guards, inside
    /// the transaction: the group must exist and be non-empty; its current membership must still equal
    /// <paramref name="expectedLeafIds"/> (frozen at validation time) — any drift rolls back; and the number
    /// of rows updated must equal the expected leaf count. Writes ONLY the three <c>dat_lines</c> columns via
    /// a single <c>UPDATE … WHERE group_id = …</c>; it does not touch extension mappings (same as Single
    /// Configure when the strategy is not <c>file_extension</c>), leaf DBs, or the filesystem. Non-group
    /// dat_lines and other groups are untouched. Returns the number of leaves updated.
    /// </summary>
    public int ApplyDatGroupConfiguration(
        string                groupId,
        IReadOnlyList<string> expectedLeafIds,
        string                fileHandling,
        string                transformStrategyType,
        string?               folderTransformId)
    {
        // Structural config guard (mirrors Single Configure: release_folder needs a folder transform; none must not).
        if (transformStrategyType != "none" && transformStrategyType != "release_folder")
            throw new GroupConfigureApplyException(GroupConfigureApplyError.InvalidConfig,
                $"Unsupported transform strategy '{transformStrategyType}'.");
        if (transformStrategyType == "release_folder" && string.IsNullOrEmpty(folderTransformId))
            throw new GroupConfigureApplyException(GroupConfigureApplyError.InvalidConfig,
                "Per release folder requires a folder transform.");
        if (transformStrategyType == "none" && !string.IsNullOrEmpty(folderTransformId))
            throw new GroupConfigureApplyException(GroupConfigureApplyError.InvalidConfig,
                "The None strategy must not carry a folder transform.");

        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        // 1. Group must exist.
        using (var g = conn.CreateCommand())
        {
            g.CommandText = "SELECT COUNT(*) FROM dat_groups WHERE id = $gid COLLATE NOCASE";
            g.Parameters.AddWithValue("$gid", groupId);
            if (Convert.ToInt64(g.ExecuteScalar()) == 0)
                throw new GroupConfigureApplyException(GroupConfigureApplyError.GroupNotFound, $"Group '{groupId}' does not exist.");
        }

        // 2. Current membership.
        var current = new List<string>();
        using (var m = conn.CreateCommand())
        {
            m.CommandText = "SELECT id FROM dat_lines WHERE group_id = $gid COLLATE NOCASE";
            m.Parameters.AddWithValue("$gid", groupId);
            using var r = m.ExecuteReader();
            while (r.Read()) current.Add(r.GetString(0));
        }
        if (current.Count == 0)
            throw new GroupConfigureApplyException(GroupConfigureApplyError.EmptyGroup, $"Group '{groupId}' has no leaves.");

        // 3. Membership must still match the frozen plan (no add/remove since validation).
        var currentSet  = new HashSet<string>(current,         StringComparer.Ordinal);
        var expectedSet = new HashSet<string>(expectedLeafIds, StringComparer.Ordinal);
        if (!currentSet.SetEquals(expectedSet))
            throw new GroupConfigureApplyException(GroupConfigureApplyError.MembershipDrift,
                "Group membership changed since validation; nothing was applied.");

        // 4. Single uniform UPDATE across the group's leaves.
        int updated;
        using (var u = conn.CreateCommand())
        {
            u.CommandText = """
                UPDATE dat_lines
                SET file_handling           = $fh,
                    transform_strategy_type = $ts,
                    folder_transform_id     = $ft
                WHERE group_id = $gid COLLATE NOCASE
                """;
            u.Parameters.AddWithValue("$fh",  fileHandling);
            u.Parameters.AddWithValue("$ts",  transformStrategyType);
            u.Parameters.AddWithValue("$ft",  (object?)folderTransformId ?? DBNull.Value);
            u.Parameters.AddWithValue("$gid", groupId);
            updated = u.ExecuteNonQuery();
        }
        if (updated != expectedSet.Count)
            throw new GroupConfigureApplyException(GroupConfigureApplyError.MembershipDrift,
                $"Expected to update {expectedSet.Count} leaves but updated {updated}; rolled back.");

        // 5. Commit — all leaves, atomically.
        tx.Commit();
        return updated;
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

    public void SaveDatLineFileHandling(string datLineId, string fileHandling)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dat_lines
            SET file_handling = $fileHandling
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id",           datLineId);
        cmd.Parameters.AddWithValue("$fileHandling", fileHandling);
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
            WHERE hardware_family_id = $pid
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
            SELECT id, label, hardware_family_id, dat_line_id, status, planned_size_bytes, actual_size_bytes, created_at, verified_at, health
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
            SELECT v.id, v.label, v.hardware_family_id, v.dat_line_id, v.status,
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
            INSERT INTO volumes(id, label, hardware_family_id, dat_line_id, status, planned_size_bytes, actual_size_bytes, created_at, verified_at, health)
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

    /// <summary>
    /// Returns all derived_artifact_ids assigned for the given dat_line_id, mapped to their
    /// volume label. Artifacts assigned to a deleted volume map to "(stale assignment)".
    /// </summary>
    public Dictionary<string, string> GetAssignedDerivedIdsWithVolumesByDatLine(string datLineId)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT va.derived_artifact_id, v.label
            FROM volume_artifacts va
            LEFT JOIN volumes v ON v.id = va.volume_id
            WHERE va.dat_line_id = $dlid
            """;
        cmd.Parameters.AddWithValue("$dlid", datLineId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.TryAdd(r.GetString(0), r.IsDBNull(1) ? "(stale assignment)" : r.GetString(1));
        return result;
    }

    /// <summary>
    /// Returns all current volume assignments for this DAT line, including the disk_id needed
    /// for full path resolution (workspace or mounted disk).
    /// Multiple rows per daId are possible when the same artifact is on more than one volume.
    /// Workspace locations are ordered first so callers can prefer them when building a
    /// resolved lookup via <see cref="VolumePathResolver.Resolve"/>.
    /// </summary>
    public List<(string DaId, string VolumeId, string VolumeLabel, string? DiskId)>
        GetAllAssignmentsForDatLine(string datLineId)
    {
        var result = new List<(string, string, string, string?)>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT va.derived_artifact_id, v.id, v.label, vl.disk_id
            FROM volume_artifacts va
            JOIN volumes v ON v.id = va.volume_id
            LEFT JOIN volume_locations vl ON vl.volume_id = v.id AND vl.is_current = 1
            WHERE va.dat_line_id = $dlid
            ORDER BY CASE vl.location_type WHEN 'workspace' THEN 0 ELSE 1 END, v.label
            """;
        cmd.Parameters.AddWithValue("$dlid", datLineId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3)));
        return result;
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

    /// <summary>
    /// Atomically inserts a volume_artifact row and increments volumes.actual_size_bytes.
    /// Does nothing if the row already exists (ON CONFLICT DO NOTHING).
    /// </summary>
    public void AddVolumeArtifactAndIncrementSize(VolumeArtifactRecord va, long sizeBytes)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO volume_artifacts(id, volume_id, dat_line_id, derived_artifact_id, content_identity_key, status, added_at_utc)
            VALUES($id, $volId, $dlid, $daId, $cik, $status, $at)
            ON CONFLICT(volume_id, derived_artifact_id) DO NOTHING
            """;
        ins.Parameters.AddWithValue("$id",     va.Id);
        ins.Parameters.AddWithValue("$volId",  va.VolumeId);
        ins.Parameters.AddWithValue("$dlid",   va.DatLineId);
        ins.Parameters.AddWithValue("$daId",   va.DerivedArtifactId);
        ins.Parameters.AddWithValue("$cik",    va.ContentIdentityKey);
        ins.Parameters.AddWithValue("$status", va.Status);
        ins.Parameters.AddWithValue("$at",     va.AddedAtUtc.ToString("o"));
        var inserted = ins.ExecuteNonQuery();

        if (inserted > 0)
        {
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE volumes SET actual_size_bytes = actual_size_bytes + $bytes WHERE id = $vid
                """;
            upd.Parameters.AddWithValue("$bytes", sizeBytes);
            upd.Parameters.AddWithValue("$vid",   va.VolumeId);
            upd.ExecuteNonQuery();
        }

        tx.Commit();
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
                v.id, v.label, v.hardware_family_id, v.dat_line_id, v.status,
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

    // ── Volume full-scan support ──────────────────────────────────────────────

    /// <summary>
    /// Returns the non-lost volumes that own the given derived artifact,
    /// excluding <paramref name="excludeVolumeId"/> (the volume currently being verified).
    /// Used to classify KNOWN_UNEXPECTED files during full-scan verify.
    /// </summary>
    public List<(string VolumeId, string VolumeLabel)> GetOwningVolumesForArtifact(
        string derivedArtifactId, string excludeVolumeId)
    {
        var result = new List<(string, string)>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT v.id, v.label
            FROM volume_artifacts va
            JOIN volumes v ON v.id = va.volume_id
            WHERE va.derived_artifact_id = $did
              AND v.status != 'lost'
              AND v.id != $excl
            ORDER BY v.label
            """;
        cmd.Parameters.AddWithValue("$did",  derivedArtifactId);
        cmd.Parameters.AddWithValue("$excl", excludeVolumeId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((r.GetString(0), r.GetString(1)));
        return result;
    }

    // ── Purge support ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns volume_artifact rows for a given derived artifact ID across all volumes.
    /// Used by the purge planner to discover which volumes hold a release's artifacts.
    /// </summary>
    public List<VolumeArtifactRecord> GetVolumeArtifactsByDerivedId(string derivedArtifactId)
    {
        var list = new List<VolumeArtifactRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, volume_id, dat_line_id, derived_artifact_id, content_identity_key, status, added_at_utc
            FROM volume_artifacts
            WHERE derived_artifact_id = $did
            """;
        cmd.Parameters.AddWithValue("$did", derivedArtifactId);
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
    /// Returns a single volume by ID, or null if not found.
    /// </summary>
    public VolumeRecord? GetVolumeById(string volumeId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, hardware_family_id, dat_line_id, status,
                   planned_size_bytes, actual_size_bytes, created_at, verified_at, health
            FROM volumes WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", volumeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadVolume(r) : null;
    }

    /// <summary>
    /// Hard-deletes a single volume_artifact row by its ID.
    /// Call only after the physical file has been confirmed absent.
    /// Adjusts volumes.actual_size_bytes by decrementing <paramref name="artifactBytes"/>.
    /// </summary>
    public void DeleteVolumeArtifactRow(string volumeArtifactId, string volumeId, long artifactBytes)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM volume_artifacts WHERE id = $id";
        del.Parameters.AddWithValue("$id", volumeArtifactId);
        del.ExecuteNonQuery();

        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = """
            UPDATE volumes
            SET actual_size_bytes = MAX(0, actual_size_bytes - $bytes)
            WHERE id = $vid
            """;
        upd.Parameters.AddWithValue("$bytes", artifactBytes);
        upd.Parameters.AddWithValue("$vid",   volumeId);
        upd.ExecuteNonQuery();

        tx.Commit();
    }

    /// <summary>
    /// Atomically moves a volume_artifact to a different volume and adjusts both volumes'
    /// actual_size_bytes in a single transaction.
    /// Call only after the physical file operation (move or copy+delete) is confirmed.
    /// </summary>
    public void MoveVolumeArtifactToVolume(
        string volumeArtifactId,
        string sourceVolumeId,
        string targetVolumeId,
        long   sizeBytes)
    {
        using var conn = Open();
        using var tx   = conn.BeginTransaction();

        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE volume_artifacts SET volume_id = $tgt WHERE id = $id";
        upd.Parameters.AddWithValue("$tgt", targetVolumeId);
        upd.Parameters.AddWithValue("$id",  volumeArtifactId);
        upd.ExecuteNonQuery();

        using var decSrc = conn.CreateCommand();
        decSrc.Transaction = tx;
        decSrc.CommandText = """
            UPDATE volumes
            SET actual_size_bytes = MAX(0, actual_size_bytes - $bytes)
            WHERE id = $vid
            """;
        decSrc.Parameters.AddWithValue("$bytes", sizeBytes);
        decSrc.Parameters.AddWithValue("$vid",   sourceVolumeId);
        decSrc.ExecuteNonQuery();

        using var incTgt = conn.CreateCommand();
        incTgt.Transaction = tx;
        incTgt.CommandText = """
            UPDATE volumes
            SET actual_size_bytes = actual_size_bytes + $bytes
            WHERE id = $vid
            """;
        incTgt.Parameters.AddWithValue("$bytes", sizeBytes);
        incTgt.Parameters.AddWithValue("$vid",   targetVolumeId);
        incTgt.ExecuteNonQuery();

        tx.Commit();
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

    // ── Working state ─────────────────────────────────────────────────────────

    /// <summary>Returns the working-state row for <paramref name="itemId"/>, or null if absent.</summary>
    public WorkingStateRecord? GetWorkingState(string itemId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT item_id, working_state, working_note, is_manual
            FROM catalog_working_state
            WHERE item_id = $itemId
            """;
        cmd.Parameters.AddWithValue("$itemId", itemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new WorkingStateRecord(
            r.GetString(0),
            r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.GetInt32(3) != 0);
    }

    /// <summary>
    /// Unconditional upsert — overwrites any existing row including manual ones.
    /// Use only when the caller has explicit user intent (e.g. a manual edit UI).
    /// </summary>
    public void SetWorkingState(WorkingStateRecord record)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_working_state(item_id, working_state, working_note, is_manual)
            VALUES($itemId, $state, $note, $isManual)
            ON CONFLICT(item_id) DO UPDATE SET
                working_state = excluded.working_state,
                working_note  = excluded.working_note,
                is_manual     = excluded.is_manual
            """;
        cmd.Parameters.AddWithValue("$itemId",   record.ItemId);
        cmd.Parameters.AddWithValue("$state",    record.State);
        cmd.Parameters.AddWithValue("$note",     (object?)record.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$isManual", record.IsManual ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Automated upsert — inserts or updates the working state only when the existing row
    /// is not manually curated (<c>is_manual = 0</c>). Safe to call from MAME imports
    /// or any automated pipeline; never clobbers a user-managed entry.
    /// </summary>
    public void SetWorkingStateIfNotManual(string itemId, string state, string? note = null)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_working_state(item_id, working_state, working_note, is_manual)
            VALUES($itemId, $state, $note, 0)
            ON CONFLICT(item_id) DO UPDATE SET
                working_state = excluded.working_state,
                working_note  = excluded.working_note
            WHERE is_manual = 0
            """;
        cmd.Parameters.AddWithValue("$itemId", itemId);
        cmd.Parameters.AddWithValue("$state",  state);
        cmd.Parameters.AddWithValue("$note",   (object?)note ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ── Metadata Value Mappings ───────────────────────────────────────────────

    public List<MetadataValueMappingRecord> LoadMetadataValueMappings()
    {
        var list = new List<MetadataValueMappingRecord>();
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT field, match_value, replacement, enabled FROM metadata_value_mappings";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new MetadataValueMappingRecord(
                Field:       reader.GetString(0),
                MatchValue:  reader.GetString(1),
                Replacement: reader.GetString(2),
                Enabled:     reader.GetInt32(3) != 0));
        return list;
    }

    public void SaveMetadataValueMapping(string field, string matchValue, string replacement, bool enabled)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metadata_value_mappings(field, match_value, replacement, enabled)
            VALUES($field, $matchValue, $replacement, $enabled)
            ON CONFLICT(field, match_value) DO UPDATE SET
                replacement = excluded.replacement,
                enabled     = excluded.enabled
            """;
        cmd.Parameters.AddWithValue("$field",       field);
        cmd.Parameters.AddWithValue("$matchValue",  matchValue);
        cmd.Parameters.AddWithValue("$replacement", replacement);
        cmd.Parameters.AddWithValue("$enabled",     enabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void DeleteMetadataValueMapping(string field, string matchValue)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM metadata_value_mappings WHERE field=$field AND match_value=$matchValue";
        cmd.Parameters.AddWithValue("$field",      field);
        cmd.Parameters.AddWithValue("$matchValue", matchValue);
        cmd.ExecuteNonQuery();
    }

    public string NormalizeMetadataValue(string field, string value)
        => MetadataValueNormalizer.Normalize(field, value, LoadMetadataValueMappings());

    public string DbPath => Path.Combine(_dataDir, "catalog.db");

    /// <summary>
    /// Returns true when at least one indexed cache package exists on disk,
    /// has status='indexed', and contains at least one game with a payload.
    /// </summary>
    public bool HasUsableCachePackages()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT cp.package_path
            FROM cache_packages cp
            WHERE EXISTS (
                SELECT 1 FROM cache_package_games g
                WHERE g.package_id = cp.id AND g.has_payload = 1)
            LIMIT 50
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (File.Exists(reader.GetString(0))) return true;
        }
        return false;
    }

    // ── Cache package management ──────────────────────────────────────────────

    public IReadOnlyList<ScreenScraperCachePackageRecord> LoadCachePackages()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT  cp.id,
                    cp.package_path,
                    cp.system_name,
                    cp.system_id,
                    cp.game_count,
                    cp.built_at_utc,
                    cp.indexed_at_utc,
                    (SELECT COUNT(*) FROM cache_package_media m
                     INNER JOIN cache_package_games g ON g.id = m.game_row_id
                     WHERE g.package_id = cp.id) AS media_count
            FROM    cache_packages cp
            WHERE   cp.provider = 'screenscraper'
            ORDER BY cp.indexed_at_utc DESC
            """;
        var list = new List<ScreenScraperCachePackageRecord>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var path = rdr.GetString(1);
            list.Add(new ScreenScraperCachePackageRecord(
                Id:          rdr.GetInt32(0),
                PackagePath: path,
                SystemName:  rdr.GetString(2),
                SystemId:    rdr.GetString(3),
                GameCount:   rdr.GetInt32(4),
                BuiltAt:     rdr.GetString(5),
                IndexedAt:   rdr.GetString(6),
                MediaCount:  (int)rdr.GetInt64(7),
                Status:      File.Exists(path) ? "Available" : "Missing"));
        }
        return list;
    }

    public void DetachCachePackage(int packageId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM cache_packages WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", packageId);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>Idempotent additive column migration — ignored if the column already exists.</summary>
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

    // ── Archive output validation (M1b) ────────────────────────────────────────

    /// <summary>Reads persisted archive-output validation metadata for a DAT line (nulls = unvalidated legacy).</summary>
    public DatLineArchiveOutputValidation? GetDatLineArchiveOutputValidation(string datLineId)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT archive_output_form, archive_output_validation_state,
                   archive_output_structural_fingerprint, archive_output_exclusion_fingerprint,
                   archive_output_validated_at_utc
            FROM dat_lines WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", datLineId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new DatLineArchiveOutputValidation
        {
            DatLineId             = datLineId,
            Form                  = r.IsDBNull(0) ? null : r.GetString(0),
            State                 = r.IsDBNull(1) ? null : r.GetString(1),
            StructuralFingerprint = r.IsDBNull(2) ? null : r.GetString(2),
            ExclusionFingerprint  = r.IsDBNull(3) ? null : r.GetString(3),
            ValidatedAtUtc        = r.IsDBNull(4) ? null : r.GetString(4),
        };
    }

    /// <summary>
    /// Persists archive-output validation metadata for a DAT line. Independent of the
    /// transform-strategy update path. Fingerprints/timestamp may be null (e.g. unknown form).
    /// </summary>
    public void UpdateDatLineArchiveOutputValidation(
        string datLineId, string form, string state,
        string? structuralFingerprint, string? exclusionFingerprint, string? validatedAtUtc)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dat_lines SET
                archive_output_form                   = $form,
                archive_output_validation_state       = $state,
                archive_output_structural_fingerprint = $struct,
                archive_output_exclusion_fingerprint  = $excl,
                archive_output_validated_at_utc       = $ts
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$form",   form);
        cmd.Parameters.AddWithValue("$state",  state);
        cmd.Parameters.AddWithValue("$struct", (object?)structuralFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$excl",   (object?)exclusionFingerprint  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts",     (object?)validatedAtUtc        ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id",     datLineId);
        cmd.ExecuteNonQuery();
    }
}
