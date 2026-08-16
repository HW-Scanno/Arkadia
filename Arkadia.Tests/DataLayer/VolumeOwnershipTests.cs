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
/// V-G1 volume ownership: the sum-type schema (dat_line_id XOR group_id + DB CHECK), the guarded/idempotent
/// legacy `volumes` rebuild (preserving every row + GUIDs + artifacts + locations), the app-level SaveVolume
/// invariant, and the group_id FK ON DELETE RESTRICT. Real CatalogService over temp catalogs; raw SQLite only
/// to seed a pre-migration DB and to inspect PRAGMAs / constraints.
/// </summary>
public sealed class VolumeOwnershipTests : IDisposable
{
    private readonly string _dir;
    private string DbPath => Path.Combine(_dir, "catalog.db");

    public VolumeOwnershipTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkVolOwn_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, recursive: true); } catch { } }

    private SqliteConnection OpenRaw(bool foreignKeys)
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var p = conn.CreateCommand();
        p.CommandText = $"PRAGMA foreign_keys = {(foreignKeys ? "ON" : "OFF")}";
        p.ExecuteNonQuery();
        return conn;
    }

    private static void Exec(SqliteConnection c, string sql)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    /// <summary>Hand-builds a pre-V-G1 catalog.db: legacy `volumes` (dat_line_id NOT NULL, no group_id) + a
    /// populated Single volume, one volume_artifacts row, one volume_locations row.</summary>
    private void SeedLegacyCatalog()
    {
        using (var c = OpenRaw(foreignKeys: false))
        {
            Exec(c, """
                CREATE TABLE volumes (
                    id TEXT PRIMARY KEY, label TEXT NOT NULL, hardware_family_id TEXT NOT NULL,
                    dat_line_id TEXT NOT NULL, status TEXT NOT NULL, health TEXT NOT NULL DEFAULT 'ok',
                    planned_size_bytes INTEGER NOT NULL, actual_size_bytes INTEGER NOT NULL,
                    created_at TEXT NOT NULL, verified_at TEXT);
                """);
            Exec(c, "CREATE INDEX idx_volumes_dat_line_id ON volumes(dat_line_id);");
            Exec(c, """
                CREATE TABLE volume_artifacts (
                    id TEXT PRIMARY KEY, volume_id TEXT NOT NULL, dat_line_id TEXT NOT NULL,
                    derived_artifact_id TEXT NOT NULL, content_identity_key TEXT NOT NULL DEFAULT '',
                    status TEXT NOT NULL, added_at_utc TEXT NOT NULL, UNIQUE(volume_id, derived_artifact_id));
                """);
            Exec(c, """
                CREATE TABLE volume_locations (
                    id TEXT PRIMARY KEY, volume_id TEXT NOT NULL, location_type TEXT NOT NULL,
                    disk_id TEXT, path TEXT, is_current INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
                """);
            Exec(c, """
                INSERT INTO volumes(id, label, hardware_family_id, dat_line_id, status, health,
                                    planned_size_bytes, actual_size_bytes, created_at, verified_at)
                VALUES('vol-1', 'c64-nointro-001', 'c64', 'c64-nointro', 'present', 'crit',
                       1000, 500, '2024-01-02T03:04:05.0000000Z', '2024-02-03T04:05:06.0000000Z');
                """);
            Exec(c, """
                INSERT INTO volume_artifacts(id, volume_id, dat_line_id, derived_artifact_id, content_identity_key, status, added_at_utc)
                VALUES('va-1', 'vol-1', 'c64-nointro', 'da-1', 'ck-1', 'present_in_final', '2024-01-02T03:04:05.0000000Z');
                """);
            Exec(c, """
                INSERT INTO volume_locations(id, volume_id, location_type, disk_id, path, is_current, created_at)
                VALUES('loc-1', 'vol-1', 'workspace', NULL, '/x', 1, '2024-01-02T03:04:05.0000000Z');
                """);
        }
        SqliteConnection.ClearAllPools();
    }

    private static VolumeRecord SingleVol(string id = "s1", string dl = "c64-nointro") => new()
    {
        Id = id, Label = "lbl", PlatformId = "c64", DatLineId = dl, GroupId = null,
        Status = "init", PlannedSizeBytes = 100, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
    };
    private static VolumeRecord GroupVol(string id, string gid) => new()
    {
        Id = id, Label = "lbl", PlatformId = "c64", DatLineId = null, GroupId = gid,
        Status = "init", PlannedSizeBytes = 100, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
    };

    private CatalogService WithFamilyAndGroup(string family = "c64", string gid = "c64-tosec")
    {
        var c = new CatalogService(_dir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = family, Name = "C64", Manufacturer = "Commodore", HardwareTypeId = "console" },
        });
        c.CreateDatGroup(DatGroupId.TryCreateNew(gid, out var id, out _, out _) ? id
            : throw new InvalidOperationException(), "Commodore 64 TOSEC", family, "tosec");
        return c;
    }

    // ── Migration preservation (cases 1–9) ──────────────────────────────────
    [Fact]
    public void Migration_LegacySingleVolume_PreservedWithGroupIdNull()
    {
        SeedLegacyCatalog();
        var catalog = new CatalogService(_dir);   // EnsureSchema → MigrateVolumesOwnership

        var v = Assert.Single(catalog.GetVolumes());
        Assert.Equal("vol-1", v.Id);                 // 1: same GUID
        Assert.Equal("c64-nointro-001", v.Label);    // 2
        Assert.Equal("c64-nointro", v.DatLineId);    // 3
        Assert.Null(v.GroupId);                      // 4
        Assert.Equal("present", v.Status);           // 5
        Assert.Equal(1000, v.PlannedSizeBytes);      // 6
        Assert.Equal(500, v.ActualSizeBytes);        // 6
        Assert.Equal("crit", v.Health);              // 7
        Assert.Equal(DateTime.Parse("2024-01-02T03:04:05.0000000Z").ToUniversalTime(), v.CreatedAt.ToUniversalTime());
        Assert.NotNull(v.VerifiedAt);                // 7
        Assert.True(v.IsDatLineOwned);

        // 8 + 9: artifacts and locations still associate with the same volume id
        using var c = OpenRaw(foreignKeys: false);
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM volume_artifacts WHERE id='va-1' AND volume_id='vol-1'"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM volume_locations WHERE id='loc-1' AND volume_id='vol-1'"));
    }

    [Fact] // 10: both indexes exist after migration
    public void Migration_RecreatesIndexes()
    {
        SeedLegacyCatalog();
        _ = new CatalogService(_dir);
        using var c = OpenRaw(foreignKeys: false);
        var idx = Indexes(c, "volumes");
        Assert.Contains("idx_volumes_dat_line_id", idx);
        Assert.Contains("idx_volumes_group_id", idx);
    }

    [Fact] // 11: FK integrity clean after migration; volumes carries the group_id FK
    public void Migration_ForeignKeysValid()
    {
        SeedLegacyCatalog();
        _ = new CatalogService(_dir);
        using var c = OpenRaw(foreignKeys: true);
        using (var chk = c.CreateCommand())
        {
            chk.CommandText = "PRAGMA foreign_key_check";
            using var r = chk.ExecuteReader();
            Assert.False(r.Read(), "no foreign-key violations expected");
        }
        // group_id → dat_groups FK present on the rebuilt table
        bool hasGroupFk = false;
        using (var fk = c.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_key_list(volumes)";
            using var r = fk.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(2), "dat_groups", StringComparison.OrdinalIgnoreCase)) hasGroupFk = true;
        }
        Assert.True(hasGroupFk);
    }

    [Fact] // 12: idempotent — running EnsureSchema twice keeps the row intact
    public void Migration_Idempotent()
    {
        SeedLegacyCatalog();
        _ = new CatalogService(_dir);
        _ = new CatalogService(_dir);   // second pass: group_id already present → no-op
        var v = Assert.Single(new CatalogService(_dir).GetVolumes());
        Assert.Equal("vol-1", v.Id);
        Assert.Equal("c64-nointro", v.DatLineId);
        Assert.Null(v.GroupId);
    }

    // ── CHECK constraint (cases 13–16) ───────────────────────────────────────
    [Fact] // 13
    public void Check_RejectsZeroOwner()
    {
        _ = new CatalogService(_dir);
        using var c = OpenRaw(foreignKeys: false);
        Assert.Throws<SqliteException>(() => Exec(c, InsertVol("z", dat: "NULL", grp: "NULL")));
    }

    [Fact] // 14
    public void Check_RejectsDualOwner()
    {
        _ = new CatalogService(_dir);
        using var c = OpenRaw(foreignKeys: false);   // FK off so the CHECK (not the FK) is what fires
        Assert.Throws<SqliteException>(() => Exec(c, InsertVol("d", dat: "'x'", grp: "'g'")));
    }

    [Fact] // 15
    public void Check_AcceptsSingleOwner()
    {
        _ = new CatalogService(_dir);
        using var c = OpenRaw(foreignKeys: false);
        Exec(c, InsertVol("s", dat: "'x'", grp: "NULL"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM volumes WHERE id='s'"));
    }

    [Fact] // 16
    public void Check_AcceptsGroupOwner()
    {
        _ = new CatalogService(_dir);
        using var c = OpenRaw(foreignKeys: false);
        Exec(c, InsertVol("g", dat: "NULL", grp: "'g'"));
        Assert.Equal(1L, Scalar(c, "SELECT COUNT(*) FROM volumes WHERE id='g'"));
    }

    // ── SaveVolume invariant ─────────────────────────────────────────────────
    [Fact]
    public void SaveVolume_ValidSingle_Persists()
    {
        var c = new CatalogService(_dir);
        c.SaveVolume(SingleVol());
        var v = Assert.Single(c.GetVolumes());
        Assert.True(v.IsDatLineOwned);
        Assert.False(v.IsGroupOwned);
    }

    [Fact]
    public void SaveVolume_ValidGroup_Persists()
    {
        var c = WithFamilyAndGroup();
        c.SaveVolume(GroupVol("gv", "c64-tosec"));
        var v = Assert.Single(c.GetVolumes());
        Assert.Equal("c64-tosec", v.GroupId);
        Assert.Null(v.DatLineId);
        Assert.True(v.IsGroupOwned);
    }

    [Fact] // Option C: a Group volume referencing a nonexistent group is rejected by the inline FK, no row left
    public void SaveVolume_GroupOwnedWithNonexistentGroup_Throws()
    {
        var c = WithFamilyAndGroup();   // creates real group "c64-tosec"
        Assert.Throws<SqliteException>(() => c.SaveVolume(GroupVol("bad", "nonexistent-group")));
        Assert.DoesNotContain(c.GetVolumes(), v => v.Id == "bad");   // failed save left no volume row
    }

    [Fact] // Group Create is header-only: a persisted Group volume has init/actual-0 and NO artifacts/locations
    public void GroupCreate_PersistsHeaderOnly()
    {
        var c = WithFamilyAndGroup();
        c.SaveVolume(new VolumeRecord
        {
            Id = "gh", Label = "c64-tosec-001", PlatformId = "c64", DatLineId = null, GroupId = "c64-tosec",
            Status = "init", PlannedSizeBytes = 42_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        });

        var v = c.GetVolumes().Single(x => x.Id == "gh");
        Assert.Equal("init", v.Status);
        Assert.Equal(0, v.ActualSizeBytes);
        Assert.Equal(42_000, v.PlannedSizeBytes);
        Assert.Empty(c.GetVolumeArtifacts("gh"));   // no artifact rows
        Assert.Null(c.GetCurrentLocation("gh"));    // no location rows
    }

    [Fact]
    public void SaveVolume_BothNull_Throws()
    {
        var c = new CatalogService(_dir);
        var bad = new VolumeRecord
        {
            Id = "x", Label = "l", PlatformId = "c64", DatLineId = null, GroupId = null,
            Status = "init", PlannedSizeBytes = 1, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        };
        Assert.Throws<InvalidOperationException>(() => c.SaveVolume(bad));
    }

    [Fact]
    public void SaveVolume_BothSet_Throws()
    {
        var c = new CatalogService(_dir);
        var bad = new VolumeRecord
        {
            Id = "x", Label = "l", PlatformId = "c64", DatLineId = "dl", GroupId = "g",
            Status = "init", PlannedSizeBytes = 1, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        };
        Assert.Throws<InvalidOperationException>(() => c.SaveVolume(bad));
    }

    // ── group_id FK ON DELETE RESTRICT ───────────────────────────────────────
    [Fact]
    public void GroupOwner_BlocksGroupDeletion_WhenFkEnforced()
    {
        var c = WithFamilyAndGroup();
        c.SaveVolume(GroupVol("gv", "c64-tosec"));
        using var raw = OpenRaw(foreignKeys: true);
        Assert.Throws<SqliteException>(() => Exec(raw, "DELETE FROM dat_groups WHERE id='c64-tosec'"));
    }

    // ── Targeted-mutation regression: ownership preserved when only one header field changes ──
    [Fact] // Group Resize: planned size changes, ownership/status untouched
    public void UpdateVolumePlannedSize_Group_PreservesOwnership()
    {
        var c = WithFamilyAndGroup();
        c.SaveVolume(GroupVol("gv", "c64-tosec"));   // planned 100, status init

        c.UpdateVolumePlannedSize("gv", 5_000);

        var v = c.GetVolumeById("gv")!;
        Assert.Equal(5_000, v.PlannedSizeBytes);
        Assert.Null(v.DatLineId);
        Assert.Equal("c64-tosec", v.GroupId);   // GroupId preserved (was dropped by the old full-record path)
        Assert.Equal("init", v.Status);         // only planned size changed
        Assert.Equal(0, v.ActualSizeBytes);
    }

    [Fact] // Single Resize: planned size changes, DatLineId preserved, GroupId stays null
    public void UpdateVolumePlannedSize_Single_PreservesOwnership()
    {
        var c = new CatalogService(_dir);
        c.SaveVolume(SingleVol("sv", "c64-nointro"));

        c.UpdateVolumePlannedSize("sv", 7_000);

        var v = c.GetVolumeById("sv")!;
        Assert.Equal(7_000, v.PlannedSizeBytes);
        Assert.Equal("c64-nointro", v.DatLineId);
        Assert.Null(v.GroupId);
    }

    [Fact] // Group Mark Lost: status → lost, ownership + planned size untouched
    public void UpdateVolumeStatus_Group_PreservesOwnership()
    {
        var c = WithFamilyAndGroup();
        c.SaveVolume(GroupVol("gv", "c64-tosec"));

        c.UpdateVolumeStatus("gv", "lost");

        var v = c.GetVolumeById("gv")!;
        Assert.Equal("lost", v.Status);
        Assert.Null(v.DatLineId);
        Assert.Equal("c64-tosec", v.GroupId);   // GroupId preserved — this was the runtime crash
        Assert.Equal(100, v.PlannedSizeBytes);  // only status changed
    }

    [Fact] // Single Mark Lost: status → lost, DatLineId preserved, GroupId stays null
    public void UpdateVolumeStatus_Single_PreservesOwnership()
    {
        var c = new CatalogService(_dir);
        c.SaveVolume(SingleVol("sv", "c64-nointro"));

        c.UpdateVolumeStatus("sv", "lost");

        var v = c.GetVolumeById("sv")!;
        Assert.Equal("lost", v.Status);
        Assert.Equal("c64-nointro", v.DatLineId);
        Assert.Null(v.GroupId);
    }

    [Fact] // LOST → Delete lifecycle for a Group init/empty volume
    public void GroupVolume_MarkLostThenDelete_Succeeds()
    {
        var c = WithFamilyAndGroup();
        c.SaveVolume(GroupVol("gv", "c64-tosec"));

        c.UpdateVolumeStatus("gv", "lost");
        c.DeleteVolume("gv");

        Assert.Null(c.GetVolumeById("gv"));                 // header absent
        Assert.Empty(c.GetVolumeArtifacts("gv"));           // no artifacts
        Assert.Null(c.GetCurrentLocation("gv"));            // no location
        Assert.DoesNotContain(c.GetVolumes(), v => v.Id == "gv");
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static string InsertVol(string id, string dat, string grp) => $"""
        INSERT INTO volumes(id, label, hardware_family_id, dat_line_id, group_id, status, health,
                            planned_size_bytes, actual_size_bytes, created_at, verified_at)
        VALUES('{id}', 'l', 'c64', {dat}, {grp}, 'init', 'ok', 1, 0, '2024-01-01T00:00:00.0000000Z', NULL);
        """;

    private static long Scalar(SqliteConnection c, string sql)
    { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return (long)(cmd.ExecuteScalar() ?? 0L); }

    private static List<string> Indexes(SqliteConnection c, string table)
    {
        var list = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA index_list({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(1));
        return list;
    }
}
