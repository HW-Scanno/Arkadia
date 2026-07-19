using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Archive;

/// <summary>Whether DAT-line configuration may be saved after archive-output validation.</summary>
public enum ArchiveConfigSaveDecision { CanSave, Blocked }

/// <summary>Outcome of a config-time archive-output validation.</summary>
public sealed record ArchiveConfigValidationOutcome(
    ArchiveOutputValidationResult Result,
    ArchiveConfigSaveDecision Decision,
    string Message);

/// <summary>
/// Production seam that DAT configuration calls to validate a DAT line's archive
/// output plan and persist the result. Testable without the UI.
///
/// Save policy (this phase):
///   ValidFullSet / ValidWithExclusions / Unknown → persist + CanSave.
///   CollisionUnresolved → persist the accurate state (for later gating) but return
///     Blocked with a clear message; never auto-exclude releases (the A/B review UI
///     is a later phase).
///
/// This does NOT change transform-strategy persistence or the archive writer.
/// </summary>
public sealed class DatLineArchiveOutputValidationService
{
    private readonly CatalogService _catalog;

    public DatLineArchiveOutputValidationService(CatalogService catalog) => _catalog = catalog;

    public ArchiveConfigValidationOutcome ValidateAndPersist(
        string datLineId,
        ArchiveOutputConfig config,
        IReadOnlyList<ArchiveReleaseInput> releases,
        DateTime? nowUtc = null)
    {
        var result  = ArchiveOutputValidator.Validate(config, releases);
        var formDb  = ArchiveOutputPersistenceMapping.FormToDb(result.Form);
        var stateDb = ArchiveOutputPersistenceMapping.StateToDb(result.State);
        var ts      = (nowUtc ?? DateTime.UtcNow).ToString("o");

        switch (result.State)
        {
            case ArchiveOutputValidationState.ValidFullSet:
                _catalog.UpdateDatLineArchiveOutputValidation(
                    datLineId, formDb, stateDb, result.StructuralFingerprint, null, ts);
                return new(result, ArchiveConfigSaveDecision.CanSave,
                    "Archive output validated — the full release set is collision-free.");

            case ArchiveOutputValidationState.ValidWithExclusions:
                _catalog.UpdateDatLineArchiveOutputValidation(
                    datLineId, formDb, stateDb, result.StructuralFingerprint, result.ExclusionFingerprint, ts);
                return new(result, ArchiveConfigSaveDecision.CanSave,
                    "Archive output validated for the current wanted subset (collisions resolved by exclusions).");

            case ArchiveOutputValidationState.CollisionUnresolved:
                // Persist the accurate (non-success) state so the future ingestion gate
                // can see it. Do not auto-exclude — the A/B review UI is a later phase.
                _catalog.UpdateDatLineArchiveOutputValidation(
                    datLineId, formDb, stateDb, result.StructuralFingerprint, null, ts);
                return new(result, ArchiveConfigSaveDecision.Blocked, BuildCollisionMessage(result));

            default: // Unknown — form couldn't be determined (e.g. strategy "none").
                _catalog.UpdateDatLineArchiveOutputValidation(datLineId, formDb, stateDb, null, null, ts);
                return new(result, ArchiveConfigSaveDecision.CanSave,
                    "Archive output form could not be determined (unknown) — no collision check performed.");
        }
    }

    /// <summary>
    /// Persists a validation result's form/state/fingerprints without re-validating.
    /// Used by the atomic config-save path after a collision is resolved, so the
    /// stored state matches exactly what the caller committed.
    /// </summary>
    public void PersistResult(string datLineId, ArchiveOutputValidationResult result, DateTime? nowUtc = null)
    {
        _catalog.UpdateDatLineArchiveOutputValidation(
            datLineId,
            ArchiveOutputPersistenceMapping.FormToDb(result.Form),
            ArchiveOutputPersistenceMapping.StateToDb(result.State),
            result.State == ArchiveOutputValidationState.Unknown ? null : result.StructuralFingerprint,
            result.State == ArchiveOutputValidationState.ValidWithExclusions ? result.ExclusionFingerprint : null,
            (nowUtc ?? DateTime.UtcNow).ToString("o"));
    }

    private static string BuildCollisionMessage(ArchiveOutputValidationResult result)
    {
        var groups = result.WantedSubsetCollisions;
        var lines = groups
            .Take(5)
            .Select(g => $"  • \"{g.ArchiveEntryName}\" ← " +
                         string.Join(", ", g.Candidates.Select(c => $"\"{c.ReleaseName}\"")));
        var more = groups.Count > 5 ? $"\n  …and {groups.Count - 5} more." : "";

        return "Archive output collision detected. Collision review UI is not implemented yet.\n\n" +
               $"{groups.Count} colliding archive name(s) among wanted releases:\n" +
               string.Join("\n", lines) + more +
               "\n\nResolve by marking one release in each group as Unwanted (existing curation), then re-configure.";
    }
}
