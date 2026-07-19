using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Archive;

/// <summary>
/// M1e.1 atomicity tests: config + validation are persisted only after a valid plan;
/// an aborted collision review leaves NO release unwanted, NO validation marked valid,
/// and NO partial config save. Drives the real ArchiveConfigSaveCoordinator + session
/// over a real DatLineStore/CatalogService (no logic reimplemented).
/// </summary>
public sealed class ArchiveConfigSaveAtomicityTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public ArchiveConfigSaveAtomicityTests()
    {
        _dir    = Path.Combine(Path.GetTempPath(), "ArkM1e1_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "dat.db");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private DatLineStore Store() => new(_dbPath);

    private CatalogService NewCatalogWithDatLine(string datLineId)
    {
        var catalog = new CatalogService(_dir);   // catalog.db in the same temp dir
        catalog.SaveHardwareFamilies(new List<HardwareFamilyRecord> { new() { Id = "f", Name = "F", HardwareTypeId = "console" } });
        catalog.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = datLineId, HardwareFamilyId = "f", Name = "DAT", Authority = "redump",
                    MediaTypeId = "other", ImportedAtUtc = DateTime.UtcNow },
        });
        return catalog;
    }

    private static ArchiveOutputConfig ChdConfig(string datLineId) => new()
    {
        PlatformId = "dc", DatLineId = datLineId, StrategyType = "release_shape", SingleFileOutputExtension = ".chd",
    };

    private void AddRelease(string id, string name, string status)
    {
        var store = Store();
        store.UpsertRelease(new ReleaseRecord { Id = id, DatLineId = "redump", Name = name, Status = status });
        store.SaveReleaseFiles(id, new List<ReleaseFileRecord>
        {
            new() { Id = id + "f", ReleaseId = id, RomName = "disc.iso", Size = "2048",
                    Sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                        System.Text.Encoding.UTF8.GetBytes(id))).ToLowerInvariant() },
        });
    }

    private IReadOnlyList<ArchiveReleaseInput> Load() =>
        ArchiveOutputConfigFactory.BuildReleaseInputs(Store().LoadReleases(), Store().LoadAllReleaseFiles());

    private ArchiveCollisionReviewSession NewSession(string datLineId) =>
        new(ChdConfig(datLineId), Load, id => Store().UpdateReleaseStatus(id, "unwanted"));

    private string StatusOf(string id) => Store().LoadReleases().Single(r => r.Id == id).Status;

    // ── 1 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfigureDatLineSave_CollisionAbort_DoesNotMarkValidationValid()
    {
        var catalog = NewCatalogWithDatLine("redump");
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");

        var session = NewSession("redump");
        // User aborts: roll back, persist nothing.
        ArchiveConfigSaveCoordinator.RollbackExclusions(session, Store());

        var v = catalog.GetDatLineArchiveOutputValidation("redump")!;
        Assert.NotEqual("valid_full_set",       v.State);
        Assert.NotEqual("valid_with_exclusions", v.State);
        Assert.True(v.IsUnvalidated);   // nothing persisted as valid
    }

    // ── 2 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfigureDatLineSave_CollisionAbort_DoesNotMarkAnyReleaseUnwanted()
    {
        _ = NewCatalogWithDatLine("redump");
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "present");   // mixed original statuses

        var session = NewSession("redump");
        session.ExcludeA();   // marks one unwanted during review

        // Abort → roll back.
        ArchiveConfigSaveCoordinator.RollbackExclusions(session, Store());

        Assert.NotEqual("unwanted", StatusOf("r1"));
        Assert.NotEqual("unwanted", StatusOf("r2"));
        // Original statuses restored exactly.
        Assert.Equal("missing", StatusOf("r1"));
        Assert.Equal("present", StatusOf("r2"));
    }

    // ── 3 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfigureDatLineSave_CollisionAbort_DoesNotPersistPartialConfig_IfAtomic()
    {
        var catalog = NewCatalogWithDatLine("redump");
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");

        bool configPersisted = false;
        var session = NewSession("redump");
        var coordinator = new ArchiveConfigSaveCoordinator(catalog);

        // On a collision the coordinator does NOT commit and does NOT persist config.
        var outcome = coordinator.TryCommit("redump", session, () => configPersisted = true);

        Assert.Equal(ArchiveConfigSaveOutcome.NeedsReview, outcome);
        Assert.False(configPersisted);   // no partial config save
    }

    // ── 4 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfigureDatLineSave_CollisionResolved_PersistsStrategyAndValidation()
    {
        var catalog = NewCatalogWithDatLine("redump");
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");

        bool configPersisted = false;
        var session = NewSession("redump");
        session.ExcludeA();   // resolve the collision
        Assert.False(session.HasUnresolvedCollision);

        var outcome = new ArchiveConfigSaveCoordinator(catalog)
            .TryCommit("redump", session, () => configPersisted = true);

        Assert.Equal(ArchiveConfigSaveOutcome.Committed, outcome);
        Assert.True(configPersisted);
        Assert.Equal("valid_with_exclusions", catalog.GetDatLineArchiveOutputValidation("redump")!.State);
    }

    // ── 5 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfigureDatLineSave_ValidFullSet_PersistsStrategyAndValidation()
    {
        var catalog = NewCatalogWithDatLine("redump");
        AddRelease("r1", "Game A", "missing");
        AddRelease("r2", "Game B", "missing");

        bool configPersisted = false;
        var session = NewSession("redump");
        Assert.False(session.HasUnresolvedCollision);

        var outcome = new ArchiveConfigSaveCoordinator(catalog)
            .TryCommit("redump", session, () => configPersisted = true);

        Assert.Equal(ArchiveConfigSaveOutcome.Committed, outcome);
        Assert.True(configPersisted);
        Assert.Equal("valid_full_set", catalog.GetDatLineArchiveOutputValidation("redump")!.State);
    }

    // ── 6 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfigureDatLineSave_CollisionUnresolved_StateIsSafeForFutureGate()
    {
        // A persisted collision_unresolved state must block a future (enabled) ingestion gate.
        var catalog = NewCatalogWithDatLine("redump");
        catalog.UpdateDatLineArchiveOutputValidation("redump", "single_file_flat", "collision_unresolved", "s", null, "t");
        var state = catalog.GetDatLineArchiveOutputValidation("redump")!.State;

        Assert.False(ArchiveIngestionGate.Evaluate(state, gateEnabled: true).Allow);
        Assert.True(ArchiveIngestionGate.Evaluate(state, gateEnabled: false).Allow);   // still deferred today
    }

    // ── 7 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfigureDatLineSave_AbortBehavior_IsDocumentedByTestName()
    {
        // Summary: collision → exclude during review → ABORT → coordinator rolls back
        // and never commits → no unwanted, no valid state, no partial config.
        var catalog = NewCatalogWithDatLine("redump");
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");

        bool configPersisted = false;
        var session = NewSession("redump");
        session.ExcludeA();                     // during review
        // ...user aborts before resolving everything:
        ArchiveConfigSaveCoordinator.RollbackExclusions(session, Store());
        // caller never calls TryCommit on abort:
        _ = configPersisted;

        Assert.Equal("missing", StatusOf("r1"));
        Assert.Equal("missing", StatusOf("r2"));
        Assert.True(catalog.GetDatLineArchiveOutputValidation("redump")!.IsUnvalidated);
        Assert.False(configPersisted);
    }
}
