using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// Phase 2 Group DAT catalog schema + repository tests: additive schema, idempotent legacy
/// migration, dat_groups CRUD-minimal, ID immutability/collision, non-destructive foreign keys,
/// SaveDatLines metadata preservation, and Single DAT compatibility. Real CatalogService over a
/// temp catalog.db; raw SQLite is used only to inspect PRAGMAs and to demonstrate DB-level
/// constraints (no delete/membership API exists yet).
/// </summary>
public sealed class DatGroupCatalogSchemaTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public DatGroupCatalogSchemaTests()
    {
        _dir    = Path.Combine(Path.GetTempPath(), "ArkGDP2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "catalog.db");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CatalogService NewCatalog() => new(_dir);

    private CatalogService WithFamily(string familyId = "capcom")
    {
        var catalog = NewCatalog();
        catalog.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = familyId, Name = "Fam", Manufacturer = "M", HardwareTypeId = "console" },
        });
        return catalog;
    }

    private static DatGroupId Gid(string s) =>
        DatGroupId.TryCreateNew(s, out var id, out _, out _)
            ? id
            : throw new InvalidOperationException($"test id '{s}' should be valid");

    private SqliteConnection OpenRaw(bool foreignKeys = true)
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        if (foreignKeys)
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    private List<string> Columns(string table)
    {
        using var conn = OpenRaw(foreignKeys: false);
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read()) cols.Add(r.GetString(1));   // column 1 = name
        return cols;
    }

    private bool IndexExists(string name)
    {
        using var conn = OpenRaw(foreignKeys: false);
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    // ── Fresh catalog schema ────────────────────────────────────────────────────

    [Fact]
    public void FreshCatalog_HasDatGroupsTableWithExpectedColumns()
    {
        NewCatalog();
        var cols = Columns("dat_groups");
        Assert.Equal(
            new[] { "id", "display_name", "hardware_family_id", "authority", "current_revision", "created_at_utc", "updated_at_utc" }.OrderBy(x => x),
            cols.OrderBy(x => x));
    }

    [Fact]
    public void FreshCatalog_DatLinesHasAllGroupColumns()
    {
        NewCatalog();
        var cols = Columns("dat_lines");
        foreach (var c in new[]
        {
            "group_id", "relative_dat_path", "source_dat_name", "source_dat_sha256",
            "semantic_fingerprint", "semantic_fingerprint_version", "last_seen_group_revision",
        })
            Assert.Contains(c, cols);
    }

    [Fact]
    public void FreshCatalog_HasExpectedIndexes()
    {
        NewCatalog();
        Assert.True(IndexExists("idx_dat_groups_hardware_family_id"));
        Assert.True(IndexExists("idx_dat_groups_family_authority"));
        Assert.True(IndexExists("idx_dat_lines_group_id"));
    }

    [Fact]
    public void FreshCatalog_DatGroupsForeignKeyToHardwareFamiliesIsRestrict()
    {
        NewCatalog();
        using var conn = OpenRaw(foreignKeys: false);
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_list(dat_groups)";
        using var r = cmd.ExecuteReader();
        bool found = false;
        while (r.Read())
        {
            // columns: id, seq, table, from, to, on_update, on_delete, match
            if (r.GetString(2) == "hardware_families" && r.GetString(3) == "hardware_family_id")
            {
                Assert.Equal("RESTRICT", r.GetString(6));
                found = true;
            }
        }
        Assert.True(found, "dat_groups should have a FK on hardware_family_id");
    }

    [Fact]
    public void FreshCatalog_DatLinesGroupIdForeignKeyToDatGroupsIsRestrict()
    {
        NewCatalog();
        using var conn = OpenRaw(foreignKeys: false);
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_list(dat_lines)";
        using var r = cmd.ExecuteReader();
        bool found = false;
        while (r.Read())
            if (r.GetString(2) == "dat_groups" && r.GetString(3) == "group_id")
            {
                Assert.Equal("RESTRICT", r.GetString(6));
                found = true;
            }
        Assert.True(found, "dat_lines.group_id should have a FK to dat_groups");
    }

    [Fact]
    public void FreshCatalog_ReopenIsIdempotent()
    {
        NewCatalog();
        NewCatalog();
        NewCatalog();   // no exception, no duplicate columns
        Assert.Equal(7, Columns("dat_groups").Count);
    }

    // ── Legacy migration (pre-Phase-2 catalog with realistic data) ───────────────

    private void BuildLegacyCatalog()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Minimal pre-Phase-2 schema: no dat_groups, dat_lines without the 7 group columns.
        cmd.CommandText = """
            CREATE TABLE hardware_families (id TEXT PRIMARY KEY, name TEXT NOT NULL, manufacturer TEXT NOT NULL, scrape_system_id TEXT NOT NULL DEFAULT '');
            CREATE TABLE media_types (id TEXT PRIMARY KEY, name TEXT NOT NULL, sort_order INTEGER NOT NULL, is_seeded INTEGER DEFAULT 0);
            CREATE TABLE dat_lines (
                id TEXT PRIMARY KEY, hardware_family_id TEXT NOT NULL, name TEXT NOT NULL, authority TEXT NOT NULL,
                media_type_id TEXT NOT NULL DEFAULT 'other', version TEXT, storage_strategy_id TEXT,
                data_store_path TEXT NOT NULL DEFAULT '', release_count INTEGER NOT NULL DEFAULT 0, imported_at_utc TEXT NOT NULL,
                transform_strategy_type TEXT NOT NULL DEFAULT 'none', folder_transform_id TEXT,
                file_handling TEXT NOT NULL DEFAULT 'archives_pre_extraction', catalog_enabled INTEGER NOT NULL DEFAULT 1,
                library_title_mode TEXT NOT NULL DEFAULT 'dat'
            );
            INSERT INTO hardware_families(id, name, manufacturer) VALUES ('capcom','Capcom','Capcom');
            INSERT INTO media_types(id, name, sort_order) VALUES ('other','Other',0);
            INSERT INTO dat_lines(id, hardware_family_id, name, authority, media_type_id, imported_at_utc)
                VALUES ('capcom-redump-other','capcom','Legacy A','redump','other','2026-01-01T00:00:00.0000000Z');
            INSERT INTO dat_lines(id, hardware_family_id, name, authority, media_type_id, imported_at_utc)
                VALUES ('capcom-nointro-other','capcom','Legacy B','nointro','other','2026-01-02T00:00:00.0000000Z');
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void LegacyCatalog_MigratesWithoutLossOrImplicitGroups()
    {
        BuildLegacyCatalog();

        var catalog = NewCatalog();   // runs EnsureSchema → additive migration

        var lines = catalog.LoadDatLines();
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Id == "capcom-redump-other");
        Assert.Contains(lines, l => l.Id == "capcom-nointro-other");

        // New leaf columns exist and are NULL (no backfill).
        foreach (var id in new[] { "capcom-redump-other", "capcom-nointro-other" })
        {
            var meta = catalog.GetDatLineGroupMetadata(id);
            Assert.NotNull(meta);
            Assert.Null(meta!.GroupId);
            Assert.Null(meta.RelativeDatPath);
            Assert.Null(meta.SourceDatName);
            Assert.Null(meta.SourceDatSha256);
            Assert.Null(meta.SemanticFingerprint);
            Assert.Null(meta.SemanticFingerprintVersion);
            Assert.Null(meta.LastSeenGroupRevision);
        }

        // No implicit group created.
        Assert.Empty(catalog.LoadDatGroups());

        // Second initialization is idempotent.
        NewCatalog();
        Assert.Equal(2, NewCatalog().LoadDatLines().Count);
    }

    // ── Group create / read ──────────────────────────────────────────────────────

    [Fact]
    public void CreateDatGroup_StartsAtRevisionZeroWithTimestamps()
    {
        var catalog = WithFamily();
        var g = catalog.CreateDatGroup(Gid("tosec-c64"), "TOSEC — Commodore 64", "capcom", "tosec");

        Assert.Equal("tosec-c64", g.Id.Value);
        Assert.Equal(0, g.CurrentRevision);
        Assert.Equal("TOSEC — Commodore 64", g.DisplayName);
        Assert.Equal("capcom", g.HardwareFamilyId);
        Assert.Equal("tosec", g.Authority);
        Assert.True(g.UpdatedAtUtc >= g.CreatedAtUtc);
    }

    [Fact]
    public void GetAndLoad_ReturnGroupsDeterministically()
    {
        var catalog = WithFamily();
        catalog.CreateDatGroup(Gid("tosec-c64"), "C64", "capcom", "tosec");
        catalog.CreateDatGroup(Gid("tosec-vic20"), "VIC-20", "capcom", "tosec");

        var got = catalog.GetDatGroup(Gid("tosec-c64"));
        Assert.NotNull(got);
        Assert.Equal("C64", got!.DisplayName);

        var all = catalog.LoadDatGroups();
        Assert.Equal(2, all.Count);
        // deterministic order (created_at, id)
        var again = catalog.LoadDatGroups();
        Assert.Equal(all.Select(x => x.Id.Value), again.Select(x => x.Id.Value));

        Assert.Null(catalog.GetDatGroup(Gid("does-not-exist")));
        Assert.True(catalog.DatGroupExists(Gid("tosec-c64")));
        Assert.False(catalog.DatGroupExists(Gid("nope")));
    }

    // ── ID collisions / immutability ─────────────────────────────────────────────

    [Fact]
    public void CreateDatGroup_UppercaseIdVariant_RejectedByPolicy()
    {
        var catalog = WithFamily();
        // Uppercase can't be a valid new DatGroupId; wrapping a legacy value and creating must fail.
        var upper = DatGroupId.FromPersisted("TOSEC-C64");
        Assert.Throws<ArgumentException>(() => catalog.CreateDatGroup(upper, "X", "capcom", "tosec"));
    }

    [Fact]
    public void DatGroups_CaseVariantId_RejectedByDbNocaseKey()
    {
        var catalog = WithFamily();
        catalog.CreateDatGroup(Gid("tosec-c64"), "C64", "capcom", "tosec");

        // Bypass the value object and insert a case-variant directly → NOCASE PK must reject it.
        using var conn = OpenRaw();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dat_groups(id, display_name, hardware_family_id, authority, current_revision, created_at_utc, updated_at_utc)
            VALUES ('TOSEC-C64','dup','capcom','tosec',0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z')
            """;
        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(19, ex.SqliteErrorCode);   // SQLITE_CONSTRAINT (PK)
    }

    [Fact]
    public void CreateDatGroup_DuplicateId_RejectedByApi()
    {
        var catalog = WithFamily();
        catalog.CreateDatGroup(Gid("tosec-c64"), "C64", "capcom", "tosec");
        Assert.Throws<InvalidOperationException>(
            () => catalog.CreateDatGroup(Gid("tosec-c64"), "again", "capcom", "tosec"));
    }

    // ── Foreign keys / non-destructive delete ────────────────────────────────────

    [Fact]
    public void CreateDatGroup_UnknownHardwareFamily_Rejected()
    {
        var catalog = WithFamily();
        Assert.Throws<ArgumentException>(
            () => catalog.CreateDatGroup(Gid("tosec-c64"), "C64", "no-such-family", "tosec"));
    }

    [Fact]
    public void DeletingReferencedHardwareFamily_IsRestricted()
    {
        var catalog = WithFamily();
        catalog.CreateDatGroup(Gid("tosec-c64"), "C64", "capcom", "tosec");

        using var conn = OpenRaw();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM hardware_families WHERE id = 'capcom'";
        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(19, ex.SqliteErrorCode);   // FK RESTRICT
    }

    [Fact]
    public void AssigningNonexistentGroupIdToLeaf_IsRejectedByFk()
    {
        var catalog = WithFamily();
        catalog.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "capcom-redump-other", HardwareFamilyId = "capcom", Name = "A",
                    Authority = "redump", MediaTypeId = "other", ImportedAtUtc = DateTime.UtcNow },
        });

        using var conn = OpenRaw();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE dat_lines SET group_id = 'ghost-group' WHERE id = 'capcom-redump-other'";
        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(19, ex.SqliteErrorCode);   // FK
    }

    [Fact]
    public void DeletingGroupWithAssignedLeaf_IsRestricted()
    {
        var catalog = WithFamily();
        catalog.CreateDatGroup(Gid("tosec-c64"), "C64", "capcom", "tosec");
        catalog.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "leaf-1", HardwareFamilyId = "capcom", Name = "A",
                    Authority = "tosec", MediaTypeId = "other", ImportedAtUtc = DateTime.UtcNow },
        });

        using var conn = OpenRaw();
        using (var assign = conn.CreateCommand())
        {
            assign.CommandText = "UPDATE dat_lines SET group_id = 'tosec-c64' WHERE id = 'leaf-1'";
            assign.ExecuteNonQuery();   // valid FK → succeeds
        }
        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM dat_groups WHERE id = 'tosec-c64'";
        var ex = Assert.Throws<SqliteException>(() => del.ExecuteNonQuery());
        Assert.Equal(19, ex.SqliteErrorCode);   // RESTRICT: leaf still references it
    }

    // ── Display-name update ──────────────────────────────────────────────────────

    [Fact]
    public void UpdateDisplayName_ChangesOnlyNameAndTimestamp()
    {
        var catalog = WithFamily();
        catalog.CreateDatGroup(Gid("tosec-c64"), "Old", "capcom", "tosec");
        // Compare reload-vs-reload so both timestamps share the same parse semantics.
        var before = catalog.GetDatGroup(Gid("tosec-c64"))!;

        catalog.UpdateDatGroupDisplayName(Gid("tosec-c64"), "New Name");

        var g = catalog.GetDatGroup(Gid("tosec-c64"))!;
        Assert.Equal("New Name", g.DisplayName);
        Assert.Equal(before.HardwareFamilyId, g.HardwareFamilyId);
        Assert.Equal(before.Authority, g.Authority);
        Assert.Equal(0, g.CurrentRevision);
        Assert.Equal(before.CreatedAtUtc, g.CreatedAtUtc);   // creation timestamp immutable
        Assert.True(g.UpdatedAtUtc >= before.UpdatedAtUtc);
    }

    [Fact]
    public void UpdateDisplayName_NonexistentGroup_Throws()
    {
        var catalog = WithFamily();
        Assert.Throws<InvalidOperationException>(
            () => catalog.UpdateDatGroupDisplayName(Gid("ghost"), "X"));
    }

    [Fact]
    public void UpdateDisplayName_EmptyName_Throws()
    {
        var catalog = WithFamily();
        catalog.CreateDatGroup(Gid("tosec-c64"), "Old", "capcom", "tosec");
        Assert.Throws<ArgumentException>(() => catalog.UpdateDatGroupDisplayName(Gid("tosec-c64"), "   "));
    }

    // ── Single DAT compatibility ─────────────────────────────────────────────────

    [Fact]
    public void SaveDatLines_NewLeafIsSingleDat_GroupIdNull()
    {
        var catalog = WithFamily();
        catalog.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "capcom-redump-other", HardwareFamilyId = "capcom", Name = "A",
                    Authority = "redump", MediaTypeId = "other", ImportedAtUtc = DateTime.UtcNow },
        });

        var meta = catalog.GetDatLineGroupMetadata("capcom-redump-other");
        Assert.NotNull(meta);
        Assert.Null(meta!.GroupId);

        // Loading a Single DAT is unchanged and requires no new parameters.
        Assert.Single(catalog.LoadDatLines());
    }

    // ── SaveDatLines metadata preservation ───────────────────────────────────────

    [Fact]
    public void SaveDatLines_DoesNotWipeGroupMetadata()
    {
        var catalog = WithFamily();
        catalog.CreateDatGroup(Gid("tosec-c64"), "C64", "capcom", "tosec");
        catalog.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "leaf-1", HardwareFamilyId = "capcom", Name = "A",
                    Authority = "tosec", MediaTypeId = "other", ImportedAtUtc = DateTime.UtcNow },
        });

        // Assign all group metadata via controlled SQL (no public assignment API yet).
        using (var conn = OpenRaw())
        using (var set = conn.CreateCommand())
        {
            set.CommandText = """
                UPDATE dat_lines SET
                    group_id                     = 'tosec-c64',
                    relative_dat_path            = 'Games/[PRG].dat',
                    source_dat_name              = 'C64 Games (PRG).dat',
                    source_dat_sha256            = 'abc123',
                    semantic_fingerprint         = 'fp-1',
                    semantic_fingerprint_version = 1,
                    last_seen_group_revision     = 0
                WHERE id = 'leaf-1'
                """;
            set.ExecuteNonQuery();
        }

        // Normal Single-DAT save path (re-upsert of the same leaf) must not touch group columns.
        var leaf = catalog.LoadDatLines().Single(l => l.Id == "leaf-1");
        leaf.Version = "v2";
        leaf.ReleaseCount = 42;
        catalog.SaveDatLines(new List<DatLineRecord> { leaf });

        var meta = catalog.GetDatLineGroupMetadata("leaf-1")!;
        Assert.Equal("tosec-c64", meta.GroupId);
        Assert.Equal("Games/[PRG].dat", meta.RelativeDatPath);
        Assert.Equal("C64 Games (PRG).dat", meta.SourceDatName);
        Assert.Equal("abc123", meta.SourceDatSha256);
        Assert.Equal("fp-1", meta.SemanticFingerprint);
        Assert.Equal(1, meta.SemanticFingerprintVersion);
        Assert.Equal(0, meta.LastSeenGroupRevision);
        // and the base columns were updated as usual
        Assert.Equal("v2", catalog.LoadDatLines().Single(l => l.Id == "leaf-1").Version);
    }

    // ── Numeric CHECK constraints (DB-enforced) ──────────────────────────────────

    private CatalogService WithLeaf(string leafId = "leaf-1")
    {
        var catalog = WithFamily();
        catalog.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = leafId, HardwareFamilyId = "capcom", Name = "A",
                    Authority = "tosec", MediaTypeId = "other", ImportedAtUtc = DateTime.UtcNow },
        });
        return catalog;
    }

    private void ExecRaw(string sql)
    {
        using var conn = OpenRaw();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void AssertRawRejected(string sql)
    {
        var ex = Assert.Throws<SqliteException>(() => ExecRaw(sql));
        Assert.Equal(19, ex.SqliteErrorCode);   // SQLITE_CONSTRAINT (CHECK)
    }

    // current_revision >= 0

    [Fact]
    public void CurrentRevision_Zero_AcceptedViaRawInsert()
    {
        WithFamily();
        ExecRaw("""
            INSERT INTO dat_groups(id, display_name, hardware_family_id, authority, current_revision, created_at_utc, updated_at_utc)
            VALUES ('g0','G','capcom','tosec',0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z')
            """);
        Assert.True(NewCatalog().DatGroupExists(Gid("g0")));
    }

    [Fact]
    public void CurrentRevision_Negative_RejectedOnInsert()
    {
        WithFamily();
        AssertRawRejected("""
            INSERT INTO dat_groups(id, display_name, hardware_family_id, authority, current_revision, created_at_utc, updated_at_utc)
            VALUES ('gneg','G','capcom','tosec',-1,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z')
            """);
    }

    [Fact]
    public void CurrentRevision_Negative_RejectedOnUpdate()
    {
        var catalog = WithFamily();
        catalog.CreateDatGroup(Gid("tosec-c64"), "C64", "capcom", "tosec");
        AssertRawRejected("UPDATE dat_groups SET current_revision = -1 WHERE id = 'tosec-c64'");
    }

    // semantic_fingerprint_version IS NULL OR > 0

    [Fact]
    public void SemanticFingerprintVersion_Null_Accepted()
    {
        WithLeaf();
        ExecRaw("UPDATE dat_lines SET semantic_fingerprint_version = NULL WHERE id = 'leaf-1'");
        Assert.Null(NewCatalog().GetDatLineGroupMetadata("leaf-1")!.SemanticFingerprintVersion);
    }

    [Fact]
    public void SemanticFingerprintVersion_Positive_Accepted()
    {
        WithLeaf();
        ExecRaw("UPDATE dat_lines SET semantic_fingerprint_version = 1 WHERE id = 'leaf-1'");
        Assert.Equal(1, NewCatalog().GetDatLineGroupMetadata("leaf-1")!.SemanticFingerprintVersion);
    }

    [Fact]
    public void SemanticFingerprintVersion_Zero_Rejected()
    {
        WithLeaf();
        AssertRawRejected("UPDATE dat_lines SET semantic_fingerprint_version = 0 WHERE id = 'leaf-1'");
    }

    [Fact]
    public void SemanticFingerprintVersion_Negative_Rejected()
    {
        WithLeaf();
        AssertRawRejected("UPDATE dat_lines SET semantic_fingerprint_version = -3 WHERE id = 'leaf-1'");
    }

    // last_seen_group_revision IS NULL OR >= 0

    [Fact]
    public void LastSeenGroupRevision_Null_Accepted()
    {
        WithLeaf();
        ExecRaw("UPDATE dat_lines SET last_seen_group_revision = NULL WHERE id = 'leaf-1'");
        Assert.Null(NewCatalog().GetDatLineGroupMetadata("leaf-1")!.LastSeenGroupRevision);
    }

    [Fact]
    public void LastSeenGroupRevision_Zero_Accepted()
    {
        WithLeaf();
        ExecRaw("UPDATE dat_lines SET last_seen_group_revision = 0 WHERE id = 'leaf-1'");
        Assert.Equal(0, NewCatalog().GetDatLineGroupMetadata("leaf-1")!.LastSeenGroupRevision);
    }

    [Fact]
    public void LastSeenGroupRevision_Negative_Rejected()
    {
        WithLeaf();
        AssertRawRejected("UPDATE dat_lines SET last_seen_group_revision = -1 WHERE id = 'leaf-1'");
    }

    [Fact]
    public void LegacyMigration_WithConstraints_StaysIdempotent()
    {
        BuildLegacyCatalog();
        NewCatalog();   // first migration adds columns + CHECKs
        NewCatalog();   // second init idempotent, no error
        var catalog = NewCatalog();
        Assert.Equal(2, catalog.LoadDatLines().Count);
        // constraint is live after migration
        AssertRawRejected("UPDATE dat_lines SET semantic_fingerprint_version = 0 WHERE id = 'capcom-redump-other'");
    }
}
