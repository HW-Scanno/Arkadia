using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Volumes;

/// <summary>
/// The single authority for a Group volume's physical leaf segment. From the technical ids only (never
/// display name, relative_dat_path, or source folders): if the leaf DatLineId begins with
/// <c>&lt;GroupId&gt;-</c> the segment is the suffix; otherwise (a manually-overridden id that does not follow
/// the prefix convention) the segment is the full DatLineId. Not a string Replace — an exact prefix test.
/// A collision-validated map for a whole Group is produced by <see cref="BuildMap"/>; two leaves mapping to
/// the same segment (case-insensitive) or an empty/unsafe segment BLOCK the Group Apply/Build (no auto-suffix).
/// </summary>
public static class GroupRelativeLeafPath
{
    /// <summary>The physical segment for one leaf: prefix-stripped when it follows <c>GroupId-</c>, else the full id.</summary>
    public static string Segment(string groupId, string datLineId)
    {
        var prefix = groupId + "-";
        return datLineId.StartsWith(prefix, StringComparison.Ordinal)
            ? datLineId[prefix.Length..]
            : datLineId;
    }

    /// <summary>
    /// Deterministic (DatLineId Ordinal), validated <c>DatLineId → segment</c> map for all Group leaves. Returns
    /// a non-null <c>Error</c> (and an empty map) on the first problem: an empty/unsafe segment, or two leaves
    /// colliding to one segment case-insensitively. Never invents a suffix.
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Map, string? Error) BuildMap(
        string groupId, IEnumerable<string> datLineIds)
    {
        var map      = new Dictionary<string, string>(StringComparer.Ordinal);
        var bySegment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // segment → datLineId

        foreach (var datLineId in datLineIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            var segment = Segment(groupId, datLineId);

            if (!IsSafeSegment(segment))
                return (Empty, $"Leaf '{datLineId}' produces an empty or unsafe physical segment ('{segment}').");

            if (bySegment.TryGetValue(segment, out var other))
                return (Empty, $"Leaves '{other}' and '{datLineId}' both map to the physical segment '{segment}'.");

            bySegment[segment] = datLineId;
            map[datLineId]     = segment;
        }

        return (map, null);
    }

    // Technical ids are lowercase [a-z0-9-]; a valid segment is a non-empty string of the same charset (no path
    // separators, no traversal, no reserved-name risk since those are uppercase).
    private static bool IsSafeSegment(string s)
        => s.Length > 0 && s.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
