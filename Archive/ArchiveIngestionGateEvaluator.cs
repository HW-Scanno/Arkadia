using System.Collections.Generic;

namespace Arkadia.Archive;

/// <summary>Result of the non-interactive ingestion gate evaluation.</summary>
public sealed record ArchiveIngestionGateEvaluation(
    bool Allow,
    string Reason,
    ArchiveOutputValidationState EffectiveState);

/// <summary>
/// Non-interactive ingestion gate. Read-only: never touches the filesystem, DB, or
/// release statuses — it only decides whether ingestion may proceed for a DAT line.
///
/// It performs a LIVE re-validation against the current config + release set so that
/// staleness (DAT/strategy changed since config) and a reintroduced collision (an
/// excluded release restored) are both caught — not just the value stored at config
/// time. Staleness is judged by comparing the stored structural fingerprint to the
/// freshly computed one via <see cref="ArchiveOutputValidator.ComputeState"/>.
///
/// Policy (gate enabled):
///   valid_full_set / valid_with_exclusions → allow
///   collision_unresolved / stale           → block
///   unknown / legacy (no stored fingerprint) → allow (not hard-blocked this phase)
/// When the gate is disabled, everything is allowed.
/// </summary>
public static class ArchiveIngestionGateEvaluator
{
    public static ArchiveIngestionGateEvaluation Evaluate(
        ArchiveOutputConfig config,
        IReadOnlyList<ArchiveReleaseInput> releases,
        string? storedStructuralFingerprint,
        bool gateEnabled)
    {
        // Legacy / never validated: no stored fingerprint → treat as Unknown → allow.
        if (string.IsNullOrEmpty(storedStructuralFingerprint))
            return Wrap(ArchiveOutputValidationState.Unknown, gateEnabled);

        var current   = ArchiveOutputValidator.Validate(config, releases);
        var effective = ArchiveOutputValidator.ComputeState(current, storedStructuralFingerprint);
        return Wrap(effective, gateEnabled);
    }

    private static ArchiveIngestionGateEvaluation Wrap(ArchiveOutputValidationState state, bool gateEnabled)
    {
        var g = ArchiveIngestionGate.Evaluate(ArchiveOutputPersistenceMapping.StateToDb(state), gateEnabled);
        return new ArchiveIngestionGateEvaluation(g.Allow, g.Reason, state);
    }
}
