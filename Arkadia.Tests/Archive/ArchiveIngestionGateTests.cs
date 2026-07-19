using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Archive;

/// <summary>
/// M1f tests for the non-interactive ingestion gate: the pure policy helper and the
/// live-revalidation evaluator seam (the production logic RunIngestionWork calls
/// before any mutation). No gate logic is reimplemented in the tests.
/// </summary>
public sealed class ArchiveIngestionGateTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public ArchiveIngestionGateTests()
    {
        _dir    = Path.Combine(Path.GetTempPath(), "ArkM1f_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "dat.db");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private DatLineStore Store() => new(_dbPath);

    private static ArchiveOutputConfig ChdConfig() => new()
    {
        PlatformId = "dc", DatLineId = "redump", StrategyType = "release_shape", SingleFileOutputExtension = ".chd",
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

    // ── 1-6: gate policy (pure helper) ───────────────────────────────────────────

    [Fact]
    public void ArchiveIngestionGate_ValidFullSet_AllowsIngestion() =>
        Assert.True(ArchiveIngestionGate.Evaluate("valid_full_set", gateEnabled: true).Allow);

    [Fact]
    public void ArchiveIngestionGate_ValidWithExclusions_AllowsIngestion() =>
        Assert.True(ArchiveIngestionGate.Evaluate("valid_with_exclusions", gateEnabled: true).Allow);

    [Fact]
    public void ArchiveIngestionGate_CollisionUnresolved_BlocksIngestion() =>
        Assert.False(ArchiveIngestionGate.Evaluate("collision_unresolved", gateEnabled: true).Allow);

    [Fact]
    public void ArchiveIngestionGate_Stale_BlocksIngestion() =>
        Assert.False(ArchiveIngestionGate.Evaluate("stale", gateEnabled: true).Allow);

    [Fact]
    public void ArchiveIngestionGate_UnknownLegacy_AllowsIngestion_ForNow() =>
        Assert.True(ArchiveIngestionGate.Evaluate("unknown", gateEnabled: true).Allow);

    [Fact]
    public void ArchiveIngestionGate_NullLegacy_AllowsIngestion_ForNow() =>
        Assert.True(ArchiveIngestionGate.Evaluate(null, gateEnabled: true).Allow);

    // ── Evaluator seam: live re-validation states ────────────────────────────────

    [Fact]
    public void Evaluator_ValidFullSet_MatchingFingerprint_Allows()
    {
        AddRelease("r1", "Game A", "missing");
        AddRelease("r2", "Game B", "missing");
        var storedFp = ArchiveOutputValidator.Validate(ChdConfig(), Load()).StructuralFingerprint;

        var eval = ArchiveIngestionGateEvaluator.Evaluate(ChdConfig(), Load(), storedFp, gateEnabled: true);

        Assert.True(eval.Allow);
        Assert.Equal(ArchiveOutputValidationState.ValidFullSet, eval.EffectiveState);
    }

    [Fact]
    public void Evaluator_CollisionReintroducedByRestore_Blocks()
    {
        // Configured valid-with-exclusions (r2 unwanted); fingerprint captured then.
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "unwanted");
        var storedFp = ArchiveOutputValidator.Validate(ChdConfig(), Load()).StructuralFingerprint;

        // User restores r2 → the wanted subset collides again at ingestion time.
        Store().RestoreWantedRelease("r2");
        var eval = ArchiveIngestionGateEvaluator.Evaluate(ChdConfig(), Load(), storedFp, gateEnabled: true);

        Assert.False(eval.Allow);
        Assert.Equal(ArchiveOutputValidationState.CollisionUnresolved, eval.EffectiveState);
        Assert.Contains("Open DAT configuration", eval.Reason);
    }

    [Fact]
    public void Evaluator_DatChanged_StructuralFingerprintMismatch_IsStale_Blocks()
    {
        AddRelease("r1", "Game", "missing");
        var staleFp = "an-old-structural-fingerprint";   // no longer matches the current plan

        var eval = ArchiveIngestionGateEvaluator.Evaluate(ChdConfig(), Load(), staleFp, gateEnabled: true);

        Assert.False(eval.Allow);
        Assert.Equal(ArchiveOutputValidationState.Stale, eval.EffectiveState);
    }

    [Fact]
    public void Evaluator_LegacyNoFingerprint_Allows()
    {
        AddRelease("r1", "Game", "missing");
        var eval = ArchiveIngestionGateEvaluator.Evaluate(ChdConfig(), Load(), storedStructuralFingerprint: null, gateEnabled: true);

        Assert.True(eval.Allow);
        Assert.Equal(ArchiveOutputValidationState.Unknown, eval.EffectiveState);
    }

    // ── 7-10: block is read-only and actionable ──────────────────────────────────

    [Fact]
    public void IngestionGate_Block_HappensBeforeStagingMutation()
    {
        // The evaluator is read-only, so it is safe to run before any staging write.
        // A sentinel representing "staging" must be untouched by evaluating the gate.
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var storedFp = "stale-fp";   // forces a block
        var stagingSentinel = Path.Combine(_dir, "staging-marker.bin");
        File.WriteAllBytes(stagingSentinel, new byte[] { 1 });

        var eval = ArchiveIngestionGateEvaluator.Evaluate(ChdConfig(), Load(), storedFp, gateEnabled: true);

        Assert.False(eval.Allow);
        Assert.True(File.Exists(stagingSentinel));   // gate did not write/move anything
    }

    [Fact]
    public void IngestionGate_Block_DoesNotMoveIncomingFiles()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var incoming = Path.Combine(_dir, "incoming", "Game.iso");
        Directory.CreateDirectory(Path.GetDirectoryName(incoming)!);
        File.WriteAllBytes(incoming, new byte[] { 9 });

        var eval = ArchiveIngestionGateEvaluator.Evaluate(ChdConfig(), Load(), "stale-fp", gateEnabled: true);

        Assert.False(eval.Allow);
        Assert.True(File.Exists(incoming));   // incoming untouched
    }

    [Fact]
    public void IngestionGate_Block_LogsClearActionableMessage()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "missing");
        var eval = ArchiveIngestionGateEvaluator.Evaluate(ChdConfig(), Load(), "stale-fp", gateEnabled: true);

        Assert.False(eval.Allow);
        Assert.Contains("Open DAT configuration", eval.Reason);
    }

    [Fact]
    public void IngestionGate_DoesNotChangeReleaseStatuses()
    {
        AddRelease("r1", "Game", "missing");
        AddRelease("r2", "Game", "present");
        ArchiveIngestionGateEvaluator.Evaluate(ChdConfig(), Load(), "stale-fp", gateEnabled: true);

        Assert.Equal("missing", Store().LoadReleases().Single(r => r.Id == "r1").Status);
        Assert.Equal("present", Store().LoadReleases().Single(r => r.Id == "r2").Status);
    }
}
