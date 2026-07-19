using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Archive;

/// <summary>
/// M1b persistence + config-validation + gate tests. Real CatalogService (temp
/// catalog.db) with real dat_lines rows; exercises production persistence, the
/// DatLineArchiveOutputValidationService, and the ArchiveIngestionGate.
/// </summary>
public sealed class ArchiveOutputValidationPersistenceTests : IDisposable
{
    private readonly string _dir;

    public ArchiveOutputValidationPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkM1b_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── Provisioning ────────────────────────────────────────────────────────────

    private CatalogService NewCatalog() => new(_dir);

    private (CatalogService Catalog, string DatLineId) ProvisionDatLine(string label = "dl1")
    {
        var catalog = NewCatalog();
        var familyId = "fam-" + label;
        catalog.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = familyId, Name = "Fam", HardwareTypeId = "console" },
        });
        var id = "datline-" + label;
        catalog.SaveDatLines(new List<DatLineRecord>
        {
            new()
            {
                Id = id, HardwareFamilyId = familyId, Name = "Test DAT " + label,
                Authority = "redump", MediaTypeId = "other", StorageStrategyId = "chd_cd_compression",
                ImportedAtUtc = DateTime.UtcNow,
            },
        });
        return (catalog, id);
    }

    private static ReleaseFileRecord F(string rom, string sha1 = "") =>
        new() { Id = rom, ReleaseId = "r", RomName = rom, Size = "1024", Sha1 = sha1 };

    private static ArchiveReleaseInput Rel(string id, string name, string status, params ReleaseFileRecord[] files) =>
        new() { ReleaseId = id, ReleaseName = name, Status = status, Files = files.ToList() };

    private static ArchiveOutputConfig ChdConfig(string datLineId) => new()
    {
        PlatformId = "fam-dl1", DatLineId = datLineId, StrategyType = "release_shape",
        SingleFileOutputExtension = ".chd",
    };

    // ══ Persistence (1-6) ═══════════════════════════════════════════════════════

    [Fact]
    public void DatLineArchiveOutputValidation_DefaultsToUnknownForExistingRows()
    {
        var (catalog, id) = ProvisionDatLine();
        var v = catalog.GetDatLineArchiveOutputValidation(id);

        Assert.NotNull(v);
        Assert.Null(v!.Form);
        Assert.Null(v.State);
        Assert.Null(v.StructuralFingerprint);
        Assert.True(v.IsUnvalidated);
    }

    [Fact]
    public void DatLineArchiveOutputValidation_CanPersistValidFullSet()
    {
        var (catalog, id) = ProvisionDatLine();
        catalog.UpdateDatLineArchiveOutputValidation(
            id, "single_file_flat", "valid_full_set", "struct-fp", null, "2026-07-18T00:00:00Z");

        var v = catalog.GetDatLineArchiveOutputValidation(id)!;
        Assert.Equal("single_file_flat", v.Form);
        Assert.Equal("valid_full_set", v.State);
        Assert.False(v.IsUnvalidated);
    }

    [Fact]
    public void DatLineArchiveOutputValidation_CanPersistValidWithExclusions()
    {
        var (catalog, id) = ProvisionDatLine();
        catalog.UpdateDatLineArchiveOutputValidation(
            id, "single_file_flat", "valid_with_exclusions", "struct-fp", "excl-fp", "2026-07-18T00:00:00Z");

        var v = catalog.GetDatLineArchiveOutputValidation(id)!;
        Assert.Equal("valid_with_exclusions", v.State);
        Assert.Equal("excl-fp", v.ExclusionFingerprint);
    }

    [Fact]
    public void DatLineArchiveOutputValidation_PreservesStructuralFingerprint()
    {
        var (catalog, id) = ProvisionDatLine();
        catalog.UpdateDatLineArchiveOutputValidation(id, "single_file_flat", "valid_full_set", "STRUCT-123", null, "t");
        Assert.Equal("STRUCT-123", catalog.GetDatLineArchiveOutputValidation(id)!.StructuralFingerprint);
    }

    [Fact]
    public void DatLineArchiveOutputValidation_PreservesExclusionFingerprint()
    {
        var (catalog, id) = ProvisionDatLine();
        catalog.UpdateDatLineArchiveOutputValidation(id, "single_file_flat", "valid_with_exclusions", "s", "EXCL-999", "t");
        Assert.Equal("EXCL-999", catalog.GetDatLineArchiveOutputValidation(id)!.ExclusionFingerprint);
    }

    [Fact]
    public void DatLineArchiveOutputValidation_DoesNotDefaultLegacyRowsToSingleFileFlat()
    {
        var (catalog, id) = ProvisionDatLine();
        var v = catalog.GetDatLineArchiveOutputValidation(id)!;

        // Legacy row must not read as single_file_flat.
        Assert.NotEqual("single_file_flat", v.Form);
        Assert.Null(v.Form);
        Assert.Equal(ArchiveDatLineOutputForm.Unknown,
            ArchiveOutputPersistenceMapping.FormFromDb(v.Form));
    }

    // ══ Config validation service (7-11) ════════════════════════════════════════

    [Fact]
    public void ConfigureDatLineArchiveValidation_ValidFullSet_CanSave()
    {
        var (catalog, id) = ProvisionDatLine();
        var svc = new DatLineArchiveOutputValidationService(catalog);
        var releases = new[]
        {
            Rel("r1", "Game A", "missing", F("a.iso")),
            Rel("r2", "Game B", "missing", F("b.iso")),
        };

        var outcome = svc.ValidateAndPersist(id, ChdConfig(id), releases);

        Assert.Equal(ArchiveConfigSaveDecision.CanSave, outcome.Decision);
        Assert.Equal(ArchiveOutputValidationState.ValidFullSet, outcome.Result.State);

        var v = catalog.GetDatLineArchiveOutputValidation(id)!;
        Assert.Equal("valid_full_set", v.State);
        Assert.Equal("single_file_flat", v.Form);
        Assert.False(string.IsNullOrEmpty(v.StructuralFingerprint));
        Assert.False(string.IsNullOrEmpty(v.ValidatedAtUtc));
    }

    [Fact]
    public void ConfigureDatLineArchiveValidation_ValidWithExclusions_CanSave()
    {
        var (catalog, id) = ProvisionDatLine();
        var svc = new DatLineArchiveOutputValidationService(catalog);
        var releases = new[]
        {
            Rel("r1", "Game", "missing",  F("a.iso")),
            Rel("r2", "Game", "unwanted", F("b.iso")),   // resolves the collision
        };

        var outcome = svc.ValidateAndPersist(id, ChdConfig(id), releases);

        Assert.Equal(ArchiveConfigSaveDecision.CanSave, outcome.Decision);
        Assert.Equal(ArchiveOutputValidationState.ValidWithExclusions, outcome.Result.State);

        var v = catalog.GetDatLineArchiveOutputValidation(id)!;
        Assert.Equal("valid_with_exclusions", v.State);
        Assert.False(string.IsNullOrEmpty(v.ExclusionFingerprint));
    }

    [Fact]
    public void ConfigureDatLineArchiveValidation_CollisionUnresolved_BlocksSaveOrReportsClearFailure()
    {
        var (catalog, id) = ProvisionDatLine();
        var svc = new DatLineArchiveOutputValidationService(catalog);
        var releases = new[]
        {
            Rel("r1", "Game", "missing", F("a.iso")),
            Rel("r2", "Game", "missing", F("b.iso")),   // collides, no exclusion
        };

        var outcome = svc.ValidateAndPersist(id, ChdConfig(id), releases);

        Assert.Equal(ArchiveConfigSaveDecision.Blocked, outcome.Decision);
        Assert.Contains("collision", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not implemented yet", outcome.Message, StringComparison.OrdinalIgnoreCase);

        // State persisted accurately (for the future gate) — never a false success, never auto-excluded.
        var v = catalog.GetDatLineArchiveOutputValidation(id)!;
        Assert.Equal("collision_unresolved", v.State);
    }

    [Fact]
    public void ConfigureDatLineArchiveValidation_NormalExcludeAfterValidFullSet_DoesNotMakeStale()
    {
        var (catalog, id) = ProvisionDatLine();
        var svc = new DatLineArchiveOutputValidationService(catalog);

        var full = new[]
        {
            Rel("r1", "Game A", "missing", F("a.iso")),
            Rel("r2", "Game B", "missing", F("b.iso")),
        };
        svc.ValidateAndPersist(id, ChdConfig(id), full);
        var storedStruct = catalog.GetDatLineArchiveOutputValidation(id)!.StructuralFingerprint;

        // Curator excludes one release, then ingestion re-evaluates against the stored fingerprint.
        var afterExclude = new[]
        {
            Rel("r1", "Game A", "missing",  F("a.iso")),
            Rel("r2", "Game B", "unwanted", F("b.iso")),
        };
        var current = ArchiveOutputValidator.Validate(ChdConfig(id), afterExclude);

        Assert.Equal(ArchiveOutputValidationState.ValidFullSet,
            ArchiveOutputValidator.ComputeState(current, storedStruct));   // NOT stale
    }

    [Fact]
    public void ConfigureDatLineArchiveValidation_StrategyChange_MakesStale()
    {
        var (catalog, id) = ProvisionDatLine();
        var svc = new DatLineArchiveOutputValidationService(catalog);

        var releases = new[] { Rel("r1", "Game", "missing", F("game.gba")) };
        svc.ValidateAndPersist(id, ChdConfig(id), releases);
        var storedStruct = catalog.GetDatLineArchiveOutputValidation(id)!.StructuralFingerprint;

        // Strategy later changes to ZIP → structural fingerprint differs → stale.
        var zipConfig = new ArchiveOutputConfig
        {
            PlatformId = "fam-dl1", DatLineId = id, StrategyType = "release_folder",
            SingleFileOutputExtension = ".zip", FolderOutputsFolder = false,
        };
        var current = ArchiveOutputValidator.Validate(zipConfig, releases);

        Assert.Equal(ArchiveOutputValidationState.Stale,
            ArchiveOutputValidator.ComputeState(current, storedStruct));
    }

    // ══ Ingestion gate (12-13) — deferred by default ════════════════════════════

    [Fact]
    public void IngestionArchiveValidationGate_LegacyUnknown_NotHardBlockedYet_IfGateDeferred()
    {
        var (catalog, id) = ProvisionDatLine();
        var v = catalog.GetDatLineArchiveOutputValidation(id);   // legacy: state null

        // Gate deferred (current default) → always allowed.
        Assert.True(ArchiveIngestionGate.Evaluate(v?.State, gateEnabled: false).Allow);
        // Even if enabled, legacy/unknown is not hard-blocked yet.
        Assert.True(ArchiveIngestionGate.Evaluate(v?.State, gateEnabled: true).Allow);
    }

    [Fact]
    public void IngestionArchiveValidationGate_ExplicitCollisionUnresolved_Blocks_OnlyIfGateEnabled()
    {
        var (catalog, id) = ProvisionDatLine();
        catalog.UpdateDatLineArchiveOutputValidation(id, "single_file_flat", "collision_unresolved", "s", null, "t");
        var state = catalog.GetDatLineArchiveOutputValidation(id)!.State;

        // Deferred gate → allowed (no disruption this phase).
        Assert.True(ArchiveIngestionGate.Evaluate(state, gateEnabled: false).Allow);
        // Enabled gate → blocked.
        Assert.False(ArchiveIngestionGate.Evaluate(state, gateEnabled: true).Allow);
    }
}
