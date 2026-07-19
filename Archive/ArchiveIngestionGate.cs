namespace Arkadia.Archive;

/// <summary>Whether ingestion may proceed for a DAT line, per its persisted archive-output validation.</summary>
public sealed record ArchiveIngestionGateResult(bool Allow, string Reason);

/// <summary>
/// Pure, non-interactive gate consulted at the start of ingestion. It is LIVE and
/// enforced (M1f): <c>RunIngestionWork</c> evaluates it — via
/// <see cref="ArchiveIngestionGateEvaluator"/>, which re-validates the current config +
/// release set against the stored structural fingerprint — BEFORE any filesystem
/// mutation (stale cleanup, extraction, staging, source promotion, archive writes,
/// incoming moves, transforms). A block leaves incoming files and release statuses
/// untouched. This class maps a persisted/effective state string to the allow/block
/// decision; the evaluator supplies that state from the live re-validation.
///
/// Rules when the gate is ENABLED:
///   null / "" / "unknown" (legacy)                 → Allow  (legacy lines are not hard-blocked)
///   "valid_full_set" / "valid_with_exclusions"     → Allow
///   "collision_unresolved" / "stale"               → Block
/// When the gate is DISABLED, everything is allowed.
/// </summary>
public static class ArchiveIngestionGate
{
    public static ArchiveIngestionGateResult Evaluate(string? persistedState, bool gateEnabled)
    {
        if (!gateEnabled)
            return new(true, "archive-output gate deferred (not enforced this phase)");

        return persistedState switch
        {
            null or "" or "unknown" =>
                new(true, "legacy/unvalidated DAT line — not blocked yet"),
            "valid_full_set" or "valid_with_exclusions" =>
                new(true, "archive output validated"),
            "collision_unresolved" =>
                new(false, "Archive output validation is unresolved. Open DAT configuration and resolve collisions before ingestion."),
            "stale" =>
                new(false, "Archive output validation is stale. Open DAT configuration and re-save the DAT line before ingestion."),
            _ =>
                new(true, "unrecognized validation state — not blocked"),
        };
    }
}
