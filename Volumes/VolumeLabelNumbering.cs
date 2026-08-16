using System;
using System.Collections.Generic;

namespace Arkadia.Volumes;

/// <summary>
/// The single authority for volume-label auto-numbering, shared by Single and Group Create. Scans the existing
/// global label namespace for <c>{prefix}-{NNN}</c>, parses the numeric suffix (case-insensitive prefix match),
/// and returns <c>max + 1</c> — gaps are never refilled. Pure and side-effect free.
/// </summary>
public static class VolumeLabelNumbering
{
    /// <summary>Next number for <paramref name="prefix"/>: max existing <c>{prefix}-{int}</c> + 1 (1 if none).</summary>
    public static int NextNumber(IEnumerable<string> existingLabels, string prefix)
    {
        var pattern = prefix + "-";
        int max = 0;
        foreach (var label in existingLabels)
        {
            if (!label.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) continue;
            var tail = label[pattern.Length..];
            if (int.TryParse(tail, out var n) && n > max)
                max = n;
        }
        return max + 1;
    }

    /// <summary>Suggested label <c>{prefix}-{NNN:D3}</c> using <see cref="NextNumber"/>.</summary>
    public static string SuggestLabel(IEnumerable<string> existingLabels, string prefix)
        => $"{prefix}-{NextNumber(existingLabels, prefix):D3}";
}
