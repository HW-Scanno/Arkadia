using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Archive;

/// <summary>
/// M1e tests for the config validation decision and the collision review session.
/// The session runs over a real DatLineStore (temp db) so Exclude → unwanted →
/// re-validate is exercised end-to-end; no collision logic is reimplemented here.
/// </summary>
public sealed class ArchiveCollisionReviewSessionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public ArchiveCollisionReviewSessionTests()
    {
        _dir    = Path.Combine(Path.GetTempPath(), "ArkM1e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "dat.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private DatLineStore Store() => new(_dbPath);

    private static ArchiveOutputConfig ChdConfig() => new()
    {
        PlatformId = "dc", DatLineId = "redump", StrategyType = "release_shape",
        SingleFileOutputExtension = ".chd",
    };

    /// <summary>Adds a release with one .iso file. Distinct content per release (unique sha).</summary>
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

    private ArchiveCollisionReviewSession NewSession() =>
        new(ChdConfig(), Load, id => Store().UpdateReleaseStatus(id, "unwanted"));

    private string StatusOf(string id) =>
        Store().LoadReleases().Single(r => r.Id == id).Status;

    // ── 1-3: config validation decision (via the M1b service) ────────────────────

    [Fact]
    public void ConfigureDatLineArchiveValidation_ValidFullSet_AllowsSave()
    {
        AddRelease("r1", "Game A", "missing");
        AddRelease("r2", "Game B", "missing");
        var result = ArchiveOutputValidator.Validate(ChdConfig(), Load());
        Assert.Equal(ArchiveOutputValidationState.ValidFullSet, result.State);
    }

    [Fact]
    public void ConfigureDatLineArchiveValidation_ValidWithExclusions_AllowsSave()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "unwanted");
        var result = ArchiveOutputValidator.Validate(ChdConfig(), Load());
        Assert.Equal(ArchiveOutputValidationState.ValidWithExclusions, result.State);
    }

    [Fact]
    public void ConfigureDatLineArchiveValidation_CollisionUnresolved_OpensReviewOrBlocks()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var session = NewSession();
        Assert.True(session.HasUnresolvedCollision);
        Assert.Equal(ArchiveOutputValidationState.CollisionUnresolved, session.State);
    }

    // ── 4-5: dialog model / candidates ───────────────────────────────────────────

    [Fact]
    public void CollisionReviewDialogModel_TwoCandidates_HasAAndB()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var pair = NewSession().CurrentPair();
        Assert.NotNull(pair);
        Assert.Equal(2, pair!.GroupSize);
        Assert.NotEqual(pair.A.ReleaseId, pair.B.ReleaseId);
        Assert.Equal("Game.chd", pair.ArchiveEntryName);
    }

    [Fact]
    public void CollisionReviewDialogModel_CandidatesContainSourceFilesAndHashes()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var pair = NewSession().CurrentPair()!;
        Assert.Single(pair.A.SourceFiles);
        Assert.Equal("disc.iso", pair.A.SourceFiles[0].RomName);
        Assert.False(string.IsNullOrEmpty(pair.A.SourceFiles[0].Sha1));
        Assert.Equal(2048, pair.A.SourceFiles[0].SizeBytes);
    }

    // ── 6-9: exclude / abort / revalidate ────────────────────────────────────────

    [Fact]
    public void CollisionReview_ExcludeA_MarksOnlyAUnwanted()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var session = NewSession();
        var a = session.CurrentPair()!.A.ReleaseId;
        var b = session.CurrentPair()!.B.ReleaseId;

        session.ExcludeA();

        Assert.Equal("unwanted", StatusOf(a));
        Assert.NotEqual("unwanted", StatusOf(b));
    }

    [Fact]
    public void CollisionReview_ExcludeB_MarksOnlyBUnwanted()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var session = NewSession();
        var a = session.CurrentPair()!.A.ReleaseId;
        var b = session.CurrentPair()!.B.ReleaseId;

        session.ExcludeB();

        Assert.Equal("unwanted", StatusOf(b));
        Assert.NotEqual("unwanted", StatusOf(a));
    }

    [Fact]
    public void CollisionReview_Abort_DoesNotChangeStatuses()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var session = NewSession();
        session.Abort();

        Assert.Equal("missing", StatusOf("r1"));
        Assert.Equal("missing", StatusOf("r2"));
    }

    [Fact]
    public void CollisionReview_AfterExclude_RevalidatesAndClearsCollision()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var session = NewSession();
        Assert.True(session.HasUnresolvedCollision);

        session.ExcludeA();

        Assert.False(session.HasUnresolvedCollision);
        Assert.Equal(ArchiveOutputValidationState.ValidWithExclusions, session.State);
        Assert.Null(session.CurrentPair());
    }

    // ── 10: three-way iterative resolution ───────────────────────────────────────

    [Fact]
    public void CollisionReview_ThreeWayCollision_ResolvesIteratively()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        AddRelease("r3", "Game", "missing");
        var session = NewSession();

        Assert.Equal(3, session.CurrentPair()!.GroupSize);
        session.ExcludeA();                                    // 3 → 2
        Assert.True(session.HasUnresolvedCollision);
        Assert.Equal(2, session.CurrentPair()!.GroupSize);
        session.ExcludeA();                                    // 2 → 1 (resolved)
        Assert.False(session.HasUnresolvedCollision);
        Assert.Equal(ArchiveOutputValidationState.ValidWithExclusions, session.State);
    }

    // ── 11: no file deletion ─────────────────────────────────────────────────────

    [Fact]
    public void CollisionReview_DoesNotDeleteFiles()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var sentinel = Path.Combine(_dir, "keep.bin");
        File.WriteAllBytes(sentinel, new byte[] { 1 });

        NewSession().ExcludeA();

        Assert.True(File.Exists(sentinel));   // exclusion is DB-only curation
    }

    // ── 12: persist ValidWithExclusions when resolved by unwanted ────────────────

    [Fact]
    public void CollisionReview_PersistsValidWithExclusions_WhenResolvedByUnwanted()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var session = NewSession();
        session.ExcludeA();
        Assert.Equal(ArchiveOutputValidationState.ValidWithExclusions, session.State);

        // The config flow persists the resolved state via the M1b service.
        var catalog = new CatalogService(_dir);   // catalog.db in the same temp dir
        // (a dat_lines row is needed for the UPDATE to land; use a lightweight upsert)
        catalog.SaveHardwareFamilies(new List<HardwareFamilyRecord> { new() { Id = "f", Name = "F", HardwareTypeId = "console" } });
        catalog.SaveDatLines(new List<DatLineRecord> { new() { Id = "redump", HardwareFamilyId = "f", Name = "DAT", Authority = "redump", MediaTypeId = "other", ImportedAtUtc = DateTime.UtcNow } });

        var svc = new DatLineArchiveOutputValidationService(catalog);
        var outcome = svc.ValidateAndPersist("redump", ChdConfig(), Load());

        Assert.Equal(ArchiveConfigSaveDecision.CanSave, outcome.Decision);
        Assert.Equal("valid_with_exclusions", catalog.GetDatLineArchiveOutputValidation("redump")!.State);
    }

    // ── 13: ValidFullSet stays valid under normal curation ───────────────────────

    [Fact]
    public void CollisionReview_ValidFullSet_NormalCurationDoesNotBecomeStale()
    {
        AddRelease("r1", "Game A", "missing");
        AddRelease("r2", "Game B", "missing");
        var before = ArchiveOutputValidator.Validate(ChdConfig(), Load());
        Assert.Equal(ArchiveOutputValidationState.ValidFullSet, before.State);

        Store().UpdateReleaseStatus("r2", "unwanted");   // ordinary curation
        var after = ArchiveOutputValidator.Validate(ChdConfig(), Load());

        Assert.Equal(ArchiveOutputValidationState.ValidFullSet,
            ArchiveOutputValidator.ComputeState(after, before.StructuralFingerprint));   // not stale
    }
}
