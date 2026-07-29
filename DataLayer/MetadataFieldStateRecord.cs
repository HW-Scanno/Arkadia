namespace Arkadia.Data;

/// <summary>
/// Tracks the source, provider, and locked state of a single metadata field for a release.
/// Stored in release_metadata_field_state.
/// </summary>
public sealed record MetadataFieldStateRecord(
    string ReleaseId,
    string Field,
    /// <summary>One of: dat, provider, manual, merged, derived.</summary>
    string Source,
    /// <summary>Provider name when Source is "provider" or "merged"; empty otherwise.</summary>
    string Provider,
    /// <summary>When true, automated scrapes and proposals must not overwrite this field.</summary>
    bool   Locked,
    string UpdatedAtUtc
);
