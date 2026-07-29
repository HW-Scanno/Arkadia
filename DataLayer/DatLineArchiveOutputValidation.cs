namespace Arkadia.Data;

/// <summary>
/// Persisted archive-output validation metadata for a DAT line (M1b).
/// All fields are nullable: a legacy DAT line that was never validated under the
/// archive-output policy reads back as all-nulls, which callers interpret as
/// <c>unknown</c> — never <c>single_file_flat</c>.
/// </summary>
public sealed class DatLineArchiveOutputValidation
{
    public required string  DatLineId { get; init; }

    /// <summary>"unknown" | "single_file_flat" | "multi_file_release_folder" | null.</summary>
    public string? Form { get; init; }

    /// <summary>
    /// "unknown" | "valid_full_set" | "valid_with_exclusions" | "collision_unresolved" | "stale" | null.
    /// </summary>
    public string? State { get; init; }

    /// <summary>Wanted-agnostic structural fingerprint from validation time; null if never validated.</summary>
    public string? StructuralFingerprint { get; init; }

    /// <summary>Exclusion-set fingerprint; only meaningful for valid_with_exclusions.</summary>
    public string? ExclusionFingerprint { get; init; }

    /// <summary>ISO-8601 UTC timestamp of the last validation, or null.</summary>
    public string? ValidatedAtUtc { get; init; }

    /// <summary>True when no validation has been persisted (legacy / never configured under the policy).</summary>
    public bool IsUnvalidated =>
        string.IsNullOrEmpty(State) ||
        string.Equals(State, "unknown", System.StringComparison.Ordinal);
}
