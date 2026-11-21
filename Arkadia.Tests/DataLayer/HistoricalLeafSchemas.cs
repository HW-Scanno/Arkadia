namespace Arkadia.Tests.DataLayer;

/// <summary>
/// The leaf-database DDL exactly as PAST versions of <c>DatLineStore.EnsureSchema</c> created it — generated from the
/// repository history, not written by hand, so a test database built from one of these scripts is structurally what
/// that version left on disk. Used only to build isolated temporary databases with an older schema.
///
/// <para>These are the two generations the current <c>EnsureSchema</c> can bring fully up to date (anything older
/// lacks columns that were added to the CREATE TABLE text without a migration and was never upgradable in place).</para>
/// </summary>
internal static class HistoricalLeafSchemas
{
    /// <summary>
    /// Milestone v3.2 (commit 6d15eb9, 2026-04-30): the core tables and the first <c>release_metadata</c>. No provider
    /// payloads, no metadata field state / proposals, no media curation, no extra notes, no v2 metadata columns, no
    /// <c>releases.show_in_catalog</c>.
    /// </summary>
    public const string V3_2 = """
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
        """;

    /// <summary>
    /// The last schema before Milestone v8.0 (parent of commit c7e55d5, 2026-06-05): every current table and the v2
    /// <c>release_metadata</c> columns, but no <c>releases.show_in_catalog</c>. The CREATE script is byte-identical to
    /// the current one; the column statements are that version's own <c>TryAddColumn</c> list.
    /// </summary>
    public const string V7 = """
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

        ALTER TABLE release_metadata ADD COLUMN sort_title TEXT NOT NULL DEFAULT '';
        ALTER TABLE release_metadata ADD COLUMN genre TEXT NOT NULL DEFAULT '';
        ALTER TABLE release_metadata ADD COLUMN subgenre TEXT NOT NULL DEFAULT '';
        ALTER TABLE release_metadata ADD COLUMN players TEXT NOT NULL DEFAULT '';
        ALTER TABLE release_metadata ADD COLUMN release_type TEXT NOT NULL DEFAULT '';
        ALTER TABLE release_metadata ADD COLUMN rating TEXT NOT NULL DEFAULT '';
        ALTER TABLE release_metadata ADD COLUMN notes TEXT NOT NULL DEFAULT '';
        """;
}
