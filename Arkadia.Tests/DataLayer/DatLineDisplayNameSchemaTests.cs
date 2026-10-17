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
/// DAT-NAMING-1 catalog schema: additive nullable <c>dat_lines.display_name</c> (fresh, legacy migration,
/// idempotence), preservation of ids/volumes/Group data, and the Group-leaf collision hook. Temp catalogs only.
/// </summary>
public sealed class DatLineDisplayNameSchemaTests : IDisposable
{
    private readonly string _dir;
    private string DbPath => Path.Combine(_dir, "catalog.db");

    public DatLineDisplayNameSchemaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkNaming1_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, recursive: true); } catch { } }

    private SqliteConnection OpenRaw()
    {
        var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private List<(string Name, string Type, bool NotNull, string? Default)> Columns(string table)
    {
        using var c = OpenRaw();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, bool, string?)>();
        while (r.Read())
            list.Add((r.GetString(1), r.GetString(2), r.GetInt32(3) != 0, r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    private string? RawDisplayName(string id)
    {
        using var c = OpenRaw();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT display_name FROM dat_lines WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (string)v;
    }

    private CatalogService NewCatalog(string family = "nds")
    {
        var c = new CatalogService(_dir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = family, Name = family.ToUpperInvariant(), Manufacturer = "M", HardwareTypeId = "console" },
        });
        return c;
    }

    private static DatLineRecord Dl(string id, string family = "nds", string authority = "nointro", string media = "cartridge", string? display = null)
        => new()
        {
            Id = id, HardwareFamilyId = family, Name = media, Authority = authority, MediaTypeId = media,
            Version = "1", DataStorePath = $"systems/{family}/{id}.db", ReleaseCount = 3,
            ImportedAtUtc = DateTime.UtcNow, DisplayName = display,
        };

    /// <summary>Pre-DAT-NAMING-1 catalog: dat_lines WITHOUT display_name, populated, plus a legacy Single volume.</summary>
    private void SeedLegacyCatalog()
    {
        using (var c = OpenRaw())
        {
            Exec(c, "CREATE TABLE hardware_families (id TEXT PRIMARY KEY, name TEXT NOT NULL, manufacturer TEXT NOT NULL, scrape_system_id TEXT NOT NULL DEFAULT '');");
            Exec(c, "CREATE TABLE media_types (id TEXT PRIMARY KEY, name TEXT NOT NULL, sort_order INTEGER NOT NULL, is_seeded INTEGER DEFAULT 0);");
            Exec(c, """
                CREATE TABLE dat_lines (
                    id TEXT PRIMARY KEY, hardware_family_id TEXT NOT NULL, name TEXT NOT NULL, authority TEXT NOT NULL,
                    media_type_id TEXT NOT NULL DEFAULT 'other', version TEXT, storage_strategy_id TEXT,
                    data_store_path TEXT NOT NULL DEFAULT '', release_count INTEGER NOT NULL DEFAULT 0, imported_at_utc TEXT NOT NULL,
                    transform_strategy_type TEXT NOT NULL DEFAULT 'none', folder_transform_id TEXT,
                    file_handling TEXT NOT NULL DEFAULT 'archives_pre_extraction', catalog_enabled INTEGER NOT NULL DEFAULT 1,
                    library_title_mode TEXT NOT NULL DEFAULT 'dat');
                CREATE TABLE volumes (
                    id TEXT PRIMARY KEY, label TEXT NOT NULL, hardware_family_id TEXT NOT NULL,
                    dat_line_id TEXT NOT NULL, status TEXT NOT NULL, health TEXT NOT NULL DEFAULT 'ok',
                    planned_size_bytes INTEGER NOT NULL, actual_size_bytes INTEGER NOT NULL,
                    created_at TEXT NOT NULL, verified_at TEXT);
                CREATE TABLE volume_artifacts (
                    id TEXT PRIMARY KEY, volume_id TEXT NOT NULL, dat_line_id TEXT NOT NULL,
                    derived_artifact_id TEXT NOT NULL, content_identity_key TEXT NOT NULL DEFAULT '',
                    status TEXT NOT NULL, added_at_utc TEXT NOT NULL, UNIQUE(volume_id, derived_artifact_id));
                INSERT INTO hardware_families(id, name, manufacturer) VALUES ('nds','NDS','Nintendo');
                INSERT INTO media_types(id, name, sort_order) VALUES ('cartridge','Cartridge',20);
                INSERT INTO dat_lines(id, hardware_family_id, name, authority, media_type_id, data_store_path, release_count, imported_at_utc)
                    VALUES ('nds-nointro-cartridge','nds','cartridge','nointro','cartridge','systems/nds/nds-nointro-cartridge.db',42,'2026-01-01T00:00:00.0000000Z');
                INSERT INTO dat_lines(id, hardware_family_id, name, authority, media_type_id, data_store_path, release_count, imported_at_utc)
                    VALUES ('nds-redump-dvd','nds','dvd','redump','cartridge','systems/nds/nds-redump-dvd.db',7,'2026-01-02T00:00:00.0000000Z');
                INSERT INTO volumes(id, label, hardware_family_id, dat_line_id, status, health, planned_size_bytes, actual_size_bytes, created_at)
                    VALUES ('vol-1','nds-nointro-001','nds','nds-nointro-cartridge','present','ok',1000,500,'2026-01-03T00:00:00.0000000Z');
                INSERT INTO volume_artifacts(id, volume_id, dat_line_id, derived_artifact_id, status, added_at_utc)
                    VALUES ('va-1','vol-1','nds-nointro-cartridge','da-1','present_in_final','2026-01-03T00:00:00.0000000Z');
                """);
        }
        SqliteConnection.ClearAllPools();
    }

    // ── 1. Fresh schema ─────────────────────────────────────────────────────────

    [Fact]
    public void FreshCatalog_HasNullableDisplayNameTextColumn_WithNoDefault()
    {
        new CatalogService(_dir);
        var col = Assert.Single(Columns("dat_lines"), c => c.Name == "display_name");
        Assert.Equal("TEXT", col.Type);
        Assert.False(col.NotNull);
        Assert.Null(col.Default);
    }

    [Fact]
    public void FreshCatalog_AddsNoDatCodeColumn_AndKeepsNameAndIdColumns()
    {
        new CatalogService(_dir);
        var names = Columns("dat_lines").Select(c => c.Name).ToList();
        Assert.DoesNotContain("dat_code", names);
        Assert.Contains("id", names);
        Assert.Contains("name", names);
    }

    // ── 2–4. Legacy migration, idempotence, preservation ────────────────────────

    [Fact]
    public void LegacyCatalog_GainsDisplayName_AllRowsNull_IdsAndNameUnchanged()
    {
        SeedLegacyCatalog();
        Assert.DoesNotContain(Columns("dat_lines"), c => c.Name == "display_name");

        var catalog = new CatalogService(_dir);

        Assert.Contains(Columns("dat_lines"), c => c.Name == "display_name");
        var lines = catalog.LoadDatLines().OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "nds-nointro-cartridge", "nds-redump-dvd" }, lines.Select(l => l.Id));
        Assert.All(lines, l => Assert.Null(l.DisplayName));
        Assert.Equal("cartridge", lines[0].Name);          // `name` keeps its current meaning
        Assert.Equal("dvd",       lines[1].Name);
        Assert.Equal(42, lines[0].ReleaseCount);
        Assert.Equal("systems/nds/nds-nointro-cartridge.db", lines[0].DataStorePath);
        Assert.Null(RawDisplayName("nds-nointro-cartridge"));
        Assert.Null(RawDisplayName("nds-redump-dvd"));
    }

    [Fact]
    public void LegacyCatalog_PreservesVolumeAndArtifactAssociations()
    {
        SeedLegacyCatalog();
        var catalog = new CatalogService(_dir);

        var vol = Assert.Single(catalog.GetVolumes());
        Assert.Equal("vol-1", vol.Id);
        Assert.Equal("nds-nointro-cartridge", vol.DatLineId);
        Assert.Null(vol.GroupId);
        var va = Assert.Single(catalog.GetVolumeArtifacts("vol-1"));
        Assert.Equal("nds-nointro-cartridge", va.DatLineId);
        Assert.Equal("da-1", va.DerivedArtifactId);
        Assert.Contains("da-1", catalog.GetAssignedDerivedIdsByDatLine("nds-nointro-cartridge"));
    }

    [Fact]
    public void RepeatedMigration_IsIdempotent_NoDuplicateColumnNoDataChange()
    {
        SeedLegacyCatalog();
        for (int i = 0; i < 4; i++) new CatalogService(_dir);

        Assert.Single(Columns("dat_lines"), c => c.Name == "display_name");
        var catalog = new CatalogService(_dir);
        Assert.Equal(2, catalog.LoadDatLines().Count);
        Assert.Single(catalog.GetVolumes());
    }

    [Fact]
    public void Reopen_DoesNotResetAStoredDisplayName()
    {
        var c1 = NewCatalog();
        c1.SaveDatLines(new List<DatLineRecord> { Dl("nds-nointro-cartridge-main", display: "NoIntro - Main") });

        new CatalogService(_dir);
        new CatalogService(_dir);

        Assert.Equal("NoIntro - Main", RawDisplayName("nds-nointro-cartridge-main"));
    }

    // ── Persistence semantics ───────────────────────────────────────────────────

    [Fact]
    public void NewRecord_RoundTripsDisplayName_AndNullStaysNull()
    {
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord>
        {
            Dl("nds-nointro-cartridge-main", display: "NoIntro - Main"),
            Dl("nds-redump-dvd"),
        });

        var byId = c.LoadDatLines().ToDictionary(l => l.Id);
        Assert.Equal("NoIntro - Main", byId["nds-nointro-cartridge-main"].DisplayName);
        Assert.Null(byId["nds-redump-dvd"].DisplayName);
    }

    [Fact]
    public void ResavingTheFullList_NeverClobbersOrResetsDisplayName()
    {
        // Import DAT saves existing + new records together; an existing row must keep its stored name.
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord> { Dl("nds-nointro-cartridge-main", display: "NoIntro - Main") });
        using (var raw = OpenRaw())
            Exec(raw, "UPDATE dat_lines SET display_name = 'Edited Later' WHERE id = 'nds-nointro-cartridge-main'");

        var all = c.LoadDatLines();                       // loaded record carries the edited name
        all.Add(Dl("nds-nointro-other-sbi", display: "SBI"));
        c.SaveDatLines(all);

        Assert.Equal("Edited Later", RawDisplayName("nds-nointro-cartridge-main"));
        Assert.Equal("SBI",          RawDisplayName("nds-nointro-other-sbi"));

        // A stale in-memory record with a DIFFERENT/NULL DisplayName must not overwrite the stored value either.
        var stale = Dl("nds-nointro-cartridge-main", display: null);
        c.SaveDatLines(new List<DatLineRecord> { stale });
        Assert.Equal("Edited Later", RawDisplayName("nds-nointro-cartridge-main"));
    }

    [Fact]
    public void ChangingDisplayName_DoesNotChangeTheId()
    {
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord> { Dl("nds-nointro-cartridge-main", display: "A") });
        using (var raw = OpenRaw())
            Exec(raw, "UPDATE dat_lines SET display_name = 'B' WHERE id = 'nds-nointro-cartridge-main'");

        var line = Assert.Single(c.LoadDatLines());
        Assert.Equal("nds-nointro-cartridge-main", line.Id);
        Assert.Equal("B", line.DisplayName);
    }

    [Fact]
    public void LegacyRow_ResolvesToTheLegacyLabel_StoredRowResolvesToItsName()
    {
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord>
        {
            Dl("nds-nointro-cartridge"),
            Dl("nds-nointro-cartridge-main", display: "NoIntro - Main"),
        });
        var byId = c.LoadDatLines().ToDictionary(l => l.Id);

        Assert.Equal("No-Intro · CARTRIDGE",
            DatLineDisplayName.Resolve(byId["nds-nointro-cartridge"].DisplayName, "No-Intro", byId["nds-nointro-cartridge"].MediaTypeId));
        Assert.Equal("NoIntro - Main",
            DatLineDisplayName.Resolve(byId["nds-nointro-cartridge-main"].DisplayName, "No-Intro", "cartridge"));
    }

    // ── 10. DAT Group behaviour unchanged ───────────────────────────────────────

    private static GroupDatCatalogLeafCreate GroupLeaf(string id, string rel) => new()
    {
        DatLine = new DatLineRecord
        {
            Id = id, HardwareFamilyId = "c64", Name = "disk", Authority = "tosec", MediaTypeId = "other",
            Version = "1", DataStorePath = $"systems/c64/{id}.db", ReleaseCount = 0, ImportedAtUtc = DateTime.UtcNow,
        },
        RelativeDatPath = rel, SourceDatName = rel, SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
        InitialWorkingStates = Array.Empty<GroupDatInitialWorkingState>(),
    };

    [Fact]
    public void GroupCreate_LeavesHaveNullDisplayName_AndGroupDataIsUnchanged()
    {
        var c = NewCatalog("c64");
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = "c64-tosec", DisplayName = "C64 TOSEC", HardwareFamilyId = "c64", Authority = "tosec",
            Leaves = new[] { GroupLeaf("c64-tosec-games", "G/g.dat"), GroupLeaf("c64-tosec-apps", "A/a.dat") },
        });

        Assert.Equal("C64 TOSEC", c.GetDatGroup(DatGroupId.FromPersisted("c64-tosec"))!.DisplayName);
        var leaves = c.GetLeavesForGroup("c64-tosec");
        Assert.Equal(2, leaves.Count);
        Assert.All(leaves, l => Assert.Equal("c64-tosec", l.GroupMetadata.GroupId));
        Assert.Null(RawDisplayName("c64-tosec-games"));
        Assert.Null(RawDisplayName("c64-tosec-apps"));
    }

    [Fact]
    public void BuilderRejectsCollisionWithAGroupLeaf_UsingAllCatalogIds()
    {
        var c = NewCatalog("c64");
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = "c64-tosec", DisplayName = "C64 TOSEC", HardwareFamilyId = "c64", Authority = "tosec",
            Leaves = new[] { GroupLeaf("c64-tosec-games", "G/g.dat") },
        });
        c.SaveDatLines(new List<DatLineRecord> { Dl("c64-redump-other", "c64", "redump", "other") });

        // LoadDatLines is the unfiltered source: it must include Group leaves AND Single DATs.
        var all = c.LoadDatLines().Select(l => l.Id).ToList();
        Assert.Contains("c64-tosec-games", all);
        Assert.Contains("c64-redump-other", all);

        var r = SingleDatIdBuilder.Build("c64", "tosec", "games", "x", all);
        Assert.True(r.IsValid);   // different id — no false positive

        // A leaf id that happens to equal a would-be 4-part id (case-insensitively) is a collision.
        var collide = SingleDatIdBuilder.Build("c64", "tosec", "other", "games", all.Append("C64-TOSEC-OTHER-GAMES"));
        Assert.Equal(SingleDatIdError.Collision, collide.Error);
    }

    [Fact]
    public void GroupPrefixPolicy_IsNotAffected_BySingleDatIdBuilder()
    {
        // DatTechnicalIdPolicy / DatGroupId behave exactly as before.
        Assert.True(DatTechnicalIdPolicy.IsValidNew("c64-tosec"));
        Assert.False(DatTechnicalIdPolicy.IsValidNew("C64-TOSEC"));
        Assert.True(DatGroupId.TryCreateNew("c64-tosec", out _, out _, out _));
        Assert.Equal("games", Arkadia.Volumes.GroupRelativeLeafPath.Segment("c64-tosec", "c64-tosec-games"));
    }

    // ── 11. UI label contract (DAT-NAMING-2 supersedes the NAMING-1 "no UI change" pin) ─────────────

    [Fact]
    public void MainWindow_KeepsFormatDatLineName_AndRoutesSingleLabelsThroughTheResolver_ExcludingGroupLeaves()
    {
        // MainWindow.axaml.cs cannot be instantiated in a unit test, so pin the source contract instead.
        var src = File.ReadAllText(Path.Combine(FindRepoRoot(), "MainWindow.axaml.cs"));

        // The legacy formatter is untouched (it still produces Group-leaf labels).
        Assert.Contains("=> $\"{GetAuthorityName(authorityId)} · {mediaTypeId.ToUpperInvariant()}\";", src);
        Assert.DoesNotContain("DisplayName", ExtractFormatDatLineName(src));

        // Single labels use the resolver; group leaves are excluded explicitly.
        Assert.Contains("DatLineDisplayName.Resolve(dl.DisplayName", src);
        Assert.Contains("_groupLeafIds.Contains(dl.Id)", src);
        Assert.Contains("? FormatDatLineName(dl.Authority, dl.MediaTypeId)", src);
    }

    private static string ExtractFormatDatLineName(string src)
    {
        var i = src.IndexOf("private string FormatDatLineName", StringComparison.Ordinal);
        if (i < 0) return "";
        var end = src.IndexOf(';', i);          // the method is a single expression-bodied statement
        return src.Substring(i, end - i + 1);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Arkadia.sln not found");
    }
}
