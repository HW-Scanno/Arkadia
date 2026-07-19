namespace Arkadia.Archive;

/// <summary>Whether ingestion may proceed for a DAT line, per its persisted archive-output validation.</summary>
public sealed record ArchiveIngestionGateResult(bool Allow, string Reason);

/// <summary>
/// Pure, non-interactive gate a future ingestion run will consult. It is NOT wired
/// into <c>RunIngestionWork</c> in this phase — enforcement is deferred until the
/// archive writer naming change and the runtime no-overwrite guard land (otherwise
/// we would validate against a future policy the writers don't yet follow).
///
/// Rules when the gate is ENABLED:
///   null / "" / "unknown" (legacy)                 → Allow  (do not hard-block legacy yet)
///   "valid_full_set" / "valid_with_exclusions"     → Allow
///   "collision_unresolved" / "stale"               → Block
/// When the gate is DISABLED (current default), everything is allowed.
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
