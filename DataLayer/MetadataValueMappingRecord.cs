namespace Arkadia.Data;

/// <summary>
/// A single metadata value mapping rule: normalises a raw provider/DAT/manual
/// value (e.g. "wor") to a display-friendly replacement (e.g. "World").
/// Stored in catalog.db — metadata_value_mappings.
/// </summary>
public sealed record MetadataValueMappingRecord(
    string Field,
    string MatchValue,
    string Replacement,
    bool   Enabled
);
