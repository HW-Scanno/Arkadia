using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// Media-type seed tests: the additive, idempotent seed of the official media types (including the
/// new <c>tape</c>/<c>bluray</c>) and the canonical display-order realignment. Everything runs over a
/// real <see cref="CatalogService"/> on a temp catalog.db; raw SQLite is used only to simulate a
/// legacy catalog and to assert dat_line preservation. No schema, executor, parser, or runtime is
/// touched by these tests.
/// </summary>
public sealed class MediaTypeSeedTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public MediaTypeSeedTests()
    {
        _dir    = Path.Combine(Path.GetTempPath(), "ArkMT_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "catalog.db");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private CatalogService NewCatalog() => new(_dir);

    private SqliteConnection OpenRaw()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    // The official seeded set, in the exact order GetMediaTypes() must return.
    private static readonly (string Id, string Name)[] ExpectedOrder =
    {
        ("rom",       "ROM"),
        ("cartridge", "Cartridge"),
        ("tape",      "Tape"),
        ("floppy",    "Floppy"),
        ("cd",        "CD"),
        ("dvd",       "DVD"),
        ("bluray",    "Blu-ray"),
        ("hdd",       "HDD"),
        ("digital",   "Digital"),
        ("other",     "Other"),
    };

    // ── 1 + 3. Fresh catalog: exact seeded set, in the required order ────────────

    [Fact]
    public void FreshCatalog_SeedsExactlyTheExpectedMediaTypes_InOrder()
    {
        var media = NewCatalog().GetMediaTypes();
        Assert.Equal(ExpectedOrder.Select(e => e.Id), media.Select(m => m.Id));
        Assert.All(media, m => Assert.True(m.IsSeeded));
    }

    // ── 2. Display names, including Tape and Blu-ray ─────────────────────────────

    [Fact]
    public void FreshCatalog_DisplayNames_AreCorrect_IncludingTapeAndBluray()
    {
        var byId = NewCatalog().GetMediaTypes().ToDictionary(m => m.Id, m => m.Name);
        Assert.Equal("Tape",    byId["tape"]);
        Assert.Equal("Blu-ray", byId["bluray"]);
        foreach (var (id, name) in ExpectedOrder)
            Assert.Equal(name, byId[id]);
    }

    // ── 3. sort_order matches the required sequence ──────────────────────────────

    [Fact]
    public void FreshCatalog_SortOrders_MatchTheCanonicalSequence()
    {
        var byId = NewCatalog().GetMediaTypes().ToDictionary(m => m.Id, m => m.SortOrder);
        Assert.Equal(10,  byId["rom"]);
        Assert.Equal(20,  byId["cartridge"]);
        Assert.Equal(30,  byId["tape"]);
        Assert.Equal(40,  byId["floppy"]);
        Assert.Equal(50,  byId["cd"]);
        Assert.Equal(60,  byId["dvd"]);
        Assert.Equal(70,  byId["bluray"]);
        Assert.Equal(80,  byId["hdd"]);
        Assert.Equal(90,  byId["digital"]);
        Assert.Equal(100, byId["other"]);
    }

    // ── 4. Second initialization does not duplicate rows ─────────────────────────

    [Fact]
    public void SecondInitialization_DoesNotDuplicateRows()
    {
        NewCatalog();                       // first init
        var media = NewCatalog().GetMediaTypes();   // re-run EnsureSchema (seed + realign)
        Assert.Equal(ExpectedOrder.Length, media.Count);
        Assert.Single(media, m => m.Id == "tape");
        Assert.Single(media, m => m.Id == "bluray");
        // no id appears twice
        Assert.Equal(media.Select(m => m.Id).Distinct().Count(), media.Count);
    }

    // ── 5 + 7 + 8 + 9. Legacy catalog upgrade, custom protection, realign scope ──

    [Fact]
    public void LegacyCatalog_GainsTapeAndBluray_RealignsOfficialsOnly_LeavesCustomIntact()
    {
        // Simulate a catalog created before tape/bluray: the old official rows with their old
        // sort_order, plus a custom (is_seeded = 0) row.
        using (var raw = OpenRaw())
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE media_types (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, sort_order INTEGER NOT NULL, is_seeded INTEGER DEFAULT 0
                );
                INSERT INTO media_types(id, name, sort_order, is_seeded) VALUES
                    ('rom','ROM',10,1), ('cartridge','Cartridge',20,1), ('cd','CD',30,1),
                    ('dvd','DVD',40,1), ('floppy','Floppy',50,1), ('hdd','HDD',60,1),
                    ('digital','Digital',70,1), ('other','Other',99,1),
                    ('betamax','Betamax',500,0);
                """;
            cmd.ExecuteNonQuery();
        }

        var media = NewCatalog().GetMediaTypes();   // EnsureSchema upgrades in place

        // 5. both new types were added
        Assert.Contains(media, m => m is { Id: "tape",   Name: "Tape",    IsSeeded: true });
        Assert.Contains(media, m => m is { Id: "bluray", Name: "Blu-ray", IsSeeded: true });

        // 9. officials realigned to the canonical order (e.g. cd 30 → 50, floppy 50 → 40)
        var byId = media.ToDictionary(m => m.Id, m => m);
        Assert.Equal(50, byId["cd"].SortOrder);
        Assert.Equal(40, byId["floppy"].SortOrder);
        Assert.Equal(100, byId["other"].SortOrder);
        Assert.Equal(
            new[] { "rom", "cartridge", "tape", "floppy", "cd", "dvd", "bluray", "hdd", "digital", "other" },
            media.Where(m => m.Id != "betamax").Select(m => m.Id));

        // 7 + 8. custom media type is untouched: name, sort_order, and is_seeded all preserved
        var betamax = byId["betamax"];
        Assert.Equal("Betamax", betamax.Name);
        Assert.Equal(500, betamax.SortOrder);
        Assert.False(betamax.IsSeeded);
    }

    // ── 6 + 10. Existing dat_lines keep media_type_id; Other stays the default ────

    [Fact]
    public void Restart_PreservesDatLineMediaTypeIds_AndOtherRemainsDefault_NoBackfill()
    {
        NewCatalog();   // full schema + seed

        // Insert a family and two dat_lines: one explicitly 'floppy', one relying on the DEFAULT.
        using (var raw = OpenRaw())
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                INSERT INTO hardware_families(id, name, manufacturer) VALUES ('c64','C64','Commodore');
                INSERT INTO dat_lines(id, hardware_family_id, name, authority, media_type_id, imported_at_utc)
                    VALUES ('c64-tosec-floppy','c64','C64 Games','tosec','floppy','2026-01-01T00:00:00Z');
                INSERT INTO dat_lines(id, hardware_family_id, name, authority, imported_at_utc)
                    VALUES ('c64-tosec-default','c64','C64 Apps','tosec','2026-01-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        NewCatalog();   // simulate a restart → seed + realign re-run

        using var check = OpenRaw();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT id, media_type_id FROM dat_lines ORDER BY id;";
        var rows = new Dictionary<string, string>();
        using (var r = q.ExecuteReader())
            while (r.Read()) rows[r.GetString(0)] = r.GetString(1);

        Assert.Equal("other",  rows["c64-tosec-default"]);   // default preserved, never backfilled
        Assert.Equal("floppy", rows["c64-tosec-floppy"]);    // explicit media_type_id preserved
    }
}
