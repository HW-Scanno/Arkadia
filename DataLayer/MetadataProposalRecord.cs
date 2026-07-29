namespace Arkadia.Data;

/// <summary>
/// A per-field metadata value proposed by a provider.
/// Proposals accumulate in release_metadata_proposals and are applied to
/// release_metadata individually (e.g. via a merge dialog).
/// </summary>
public sealed record MetadataProposalRecord(
    string ReleaseId,
    string Provider,
    string Field,
    string Value,
    string ScrapedAt,
    bool   Accepted
);
