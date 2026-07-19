using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Archive;

/// <summary>
/// Tests for the operator batch archive-output validation action. Uses a real
/// CatalogService (catalog.db) + per-DAT DatLineStore files; exercises the real
/// ArchiveOutputBatchValidator (no logic reimplemented). Asserts it persists states,
/// reports problems, and never mutates release statuses or touches files.
/// </summary>
public sealed class ArchiveOutputBatchValidatorTests : IDisposable
{
    private readonly string _dir;      // acts as catalog dir AND data dir

    public ArchiveOutputBatchValidatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkBatch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private CatalogService Catalog() => new(_dir);

    /// <summary>Creates a release_shape DAT line with its own per-DAT store + releases.</summary>
    private (string DatLineId, string DbRelPath) ProvisionDatLine(
        CatalogService catalog, string datLineId, params (string Id, string Name, string Status)[] releases)
    {
        catalog.SaveHardwareFamilies(new List<HardwareFamilyRecord> { new() { Id = "dc", Name = "DC", HardwareTypeId = "console" } });
        var dbRel = Path.Combine("systems", "dc", datLineId + ".db");
        catalog.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = datLineId, HardwareFamilyId = "dc", Name = "DAT " + datLineId, Authority = "redump",
                    MediaTypeId = "other", DataStorePath = dbRel, ImportedAtUtc = DateTime.UtcNow },
        });
        // Strategy is persisted separately (as the real Configure flow does).
        catalog.SaveDatLineTransformStrategy(datLineId, "release_shape", null);

        var absPath = Path.Combine(_dir, dbRel);
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        var store = new DatLineStore(absPath);
        foreach (var (id, name, status) in releases)
        {
            store.UpsertRelease(new ReleaseRecord { Id = id, DatLineId = datLineId, Name = name, Status = status });
            store.SaveReleaseFiles(id, new List<ReleaseFileRecord>
            {
                new() { Id = id + "f", ReleaseId = id, RomName = "disc.iso", Size = "2048",
                        Sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                            System.Text.Encoding.UTF8.GetBytes(id))).ToLowerInvariant() },
            });
        }
        return (datLineId, dbRel);
    }

    private ArchiveOutputBatchReport Run(CatalogService catalog) =>
        new ArchiveOutputBatchValidator(catalog, _dir).ValidateAll();

    // ── clean DAT line ───────────────────────────────────────────────────────────

    [Fact]
    public void Batch_ValidatesCleanDatLine_AsValidFullSet()
    {
        var catalog = Catalog();
        ProvisionDatLine(catalog, "clean", ("r1", "Game A", "missing"), ("r2", "Game B", "missing"));

        var report = Run(catalog);

        Assert.Equal(1, report.TotalScanned);
        Assert.Equal(1, report.ValidFullSet);
        Assert.Equal("valid_full_set", catalog.GetDatLineArchiveOutputValidation("clean")!.State);
    }

    // ── collision DAT line ───────────────────────────────────────────────────────

    [Fact]
    public void Batch_DetectsCollisionDatLine()
    {
        var catalog = Catalog();
        ProvisionDatLine(catalog, "collide", ("r1", "Game", "missing"), ("r2", "Game", "missing"));

        var report = Run(catalog);

        Assert.Equal(1, report.CollisionUnresolved);
        Assert.Contains(report.Problematic, p => p.DatLineId == "collide" && p.State == "collision_unresolved");
        Assert.Equal("collision_unresolved", catalog.GetDatLineArchiveOutputValidation("collide")!.State);
    }

    // ── unwanted/exclusion semantics preserved ───────────────────────────────────

    [Fact]
    public void Batch_PreservesExclusionSemantics_ValidWithExclusions()
    {
        var catalog = Catalog();
        // Same name, one unwanted → the wanted subset is clean.
        ProvisionDatLine(catalog, "excl", ("r1", "Game", "missing"), ("r2", "Game", "unwanted"));

        var report = Run(catalog);

        Assert.Equal(1, report.ValidWithExclusions);
        Assert.Equal("valid_with_exclusions", catalog.GetDatLineArchiveOutputValidation("excl")!.State);
    }

    // ── does not mutate release statuses ─────────────────────────────────────────

    [Fact]
    public void Batch_DoesNotMutateReleaseStatuses()
    {
        var catalog = Catalog();
        var (_, dbRel) = ProvisionDatLine(catalog, "collide",
            ("r1", "Game", "missing"), ("r2", "Game", "present"));

        Run(catalog);   // collision — but must not auto-exclude

        var store = new DatLineStore(Path.Combine(_dir, dbRel));
        Assert.Equal("missing", store.LoadReleases().Single(r => r.Id == "r1").Status);
        Assert.Equal("present", store.LoadReleases().Single(r => r.Id == "r2").Status);   // no auto-unwanted
    }

    // ── does not touch files ─────────────────────────────────────────────────────

    [Fact]
    public void Batch_DoesNotTouchFiles()
    {
        var catalog = Catalog();
        ProvisionDatLine(catalog, "collide", ("r1", "Game", "missing"), ("r2", "Game", "missing"));
        var sentinel = Path.Combine(_dir, "archive-sentinel.bin");
        File.WriteAllBytes(sentinel, new byte[] { 1, 2, 3 });

        Run(catalog);

        Assert.True(File.Exists(sentinel));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(sentinel));
    }

    // ── persists valid states ────────────────────────────────────────────────────

    [Fact]
    public void Batch_PersistsValidStates_AcrossMultipleDatLines()
    {
        var catalog = Catalog();
        ProvisionDatLine(catalog, "clean1", ("a1", "Alpha", "missing"));
        ProvisionDatLine(catalog, "clean2", ("b1", "Beta", "missing"));

        var report = Run(catalog);

        Assert.Equal(2, report.TotalScanned);
        Assert.Equal(2, report.ValidFullSet);
        Assert.False(string.IsNullOrEmpty(catalog.GetDatLineArchiveOutputValidation("clean1")!.StructuralFingerprint));
        Assert.False(string.IsNullOrEmpty(catalog.GetDatLineArchiveOutputValidation("clean2")!.StructuralFingerprint));
    }

    // ── reports unresolved/unknown states ────────────────────────────────────────

    [Fact]
    public void Batch_ReportsUnknownState_WhenNoStore()
    {
        var catalog = Catalog();
        catalog.SaveHardwareFamilies(new List<HardwareFamilyRecord> { new() { Id = "dc", Name = "DC", HardwareTypeId = "console" } });
        catalog.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "nostore", HardwareFamilyId = "dc", Name = "No Store", Authority = "redump",
                    MediaTypeId = "other", DataStorePath = "", ImportedAtUtc = DateTime.UtcNow },
        });

        var report = Run(catalog);

        Assert.Equal(1, report.UnknownOrError);
        Assert.Contains(report.Problematic, p => p.DatLineId == "nostore" && p.State == "unknown");
    }

    [Fact]
    public void Batch_ReportsMixedFleet_WithCounts()
    {
        var catalog = Catalog();
        ProvisionDatLine(catalog, "clean",  ("r1", "Game A", "missing"), ("r2", "Game B", "missing"));
        ProvisionDatLine(catalog, "collide", ("c1", "Dup", "missing"), ("c2", "Dup", "missing"));
        ProvisionDatLine(catalog, "excl",    ("e1", "Dup2", "missing"), ("e2", "Dup2", "unwanted"));

        var report = Run(catalog);

        Assert.Equal(3, report.TotalScanned);
        Assert.Equal(1, report.ValidFullSet);
        Assert.Equal(1, report.ValidWithExclusions);
        Assert.Equal(1, report.CollisionUnresolved);
        Assert.Single(report.Problematic);   // only the collision line
    }
}
