using System;
using System.Collections.Generic;

namespace Arkadia.Data;

/// <summary>
/// Normalises a raw metadata value against a list of mapping rules.
/// Matching is case-insensitive; input is trimmed before matching.
/// </summary>
public static class MetadataValueNormalizer
{
    /// <summary>
    /// Returns the replacement for <paramref name="value"/> in the given
    /// <paramref name="field"/> if an enabled mapping exists, otherwise returns
    /// the trimmed input. Empty values are returned unchanged.
    /// </summary>
    public static string Normalize(
        string field,
        string value,
        IReadOnlyList<MetadataValueMappingRecord> mappings)
    {
        if (value.Length == 0) return value;

        var trimmed = value.Trim();

        foreach (var m in mappings)
        {
            if (!m.Enabled) continue;
            if (!string.Equals(m.Field, field, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(m.MatchValue, trimmed, StringComparison.OrdinalIgnoreCase))
                return m.Replacement;
        }

        return trimmed;
    }
}
