using System;
using System.Collections.Generic;

namespace Arkadia.Systems;

/// <summary>
/// A single Group-DAT card in the Systems view. A group is rendered as ONE card regardless of how many
/// leaves it has; the leaves stay real, operational dat_lines but are hidden from the main list.
/// Completion mirrors <see cref="SystemPlatform"/> exactly — summed numerators over summed denominators
/// (present ÷ wanted), never an average of per-leaf percentages.
/// </summary>
public sealed record GroupDatCardInfo(
    string GroupId,
    string DisplayName,
    string Authority,           // already resolved to a display name by the caller
    string HardwareFamilyId,
    int    LeafCount,
    int    PresentSum,
    int    WantedSum)
{
    /// <summary>Wanted coverage percent, or null when there are no wanted releases (same rule as SystemPlatform).</summary>
    public int? CoveragePercent => WantedSum > 0 ? PresentSum * 100 / WantedSum : (int?)null;

    /// <summary>Coverage as a display string; "N/A" when there are no wanted releases.</summary>
    public string CoverageText => CoveragePercent is { } pct ? $"{pct}%" : "N/A";

    /// <summary>Second card line, e.g. "TOSEC · 410 leaf DATs".</summary>
    public string Subtitle => $"{Authority} · {LeafCount} leaf DAT{(LeafCount == 1 ? "" : "s")}";
}

/// <summary>Per-leaf coverage inputs fed to <see cref="GroupDatPartition.BuildGroupCards"/> (read-only aggregation).</summary>
public readonly record struct LeafCoverageInput(string GroupId, int ReleaseCount, int Present, int Unwanted);

/// <summary>Group display metadata resolved from <c>dat_groups</c> (display name, resolved authority, System id).</summary>
public sealed record GroupMeta(string DisplayName, string Authority, string HardwareFamilyId);

/// <summary>
/// Pure Systems-view partitioning helpers: aggregate group-leaf coverage into one card per group. No I/O,
/// no catalog, no Avalonia — so the Single-vs-Group rendering rule and the summed-coverage formula are
/// unit-testable in isolation.
/// </summary>
public static class GroupDatPartition
{
    /// <summary>
    /// One <see cref="GroupDatCardInfo"/> per group that has at least one leaf in <paramref name="groupLeaves"/>.
    /// Numerator = Σ present, denominator = Σ max(0, releaseCount − unwanted) — i.e. summed numerators over
    /// summed denominators, mirroring <see cref="SystemPlatform.WantedCoveragePercent"/>.
    /// </summary>
    public static List<GroupDatCardInfo> BuildGroupCards(
        IEnumerable<LeafCoverageInput>          groupLeaves,
        IReadOnlyDictionary<string, GroupMeta>  groups)
    {
        var agg = new Dictionary<string, (int Leaves, int Present, int Wanted)>(StringComparer.OrdinalIgnoreCase);
        foreach (var lf in groupLeaves)
        {
            if (!groups.ContainsKey(lf.GroupId)) continue;   // defensive: leaf points at an unknown group
            agg.TryGetValue(lf.GroupId, out var cur);
            cur.Leaves  += 1;
            cur.Present += lf.Present;
            cur.Wanted  += Math.Max(0, lf.ReleaseCount - lf.Unwanted);
            agg[lf.GroupId] = cur;
        }

        var cards = new List<GroupDatCardInfo>(agg.Count);
        foreach (var (gid, a) in agg)
        {
            var m = groups[gid];
            cards.Add(new GroupDatCardInfo(gid, m.DisplayName, m.Authority, m.HardwareFamilyId, a.Leaves, a.Present, a.Wanted));
        }
        return cards;
    }
}
