using System;
using System.Collections.Generic;

namespace Arkadia.Systems;

/// <summary>What a toolbar item is, for overflow purposes.</summary>
public enum ToolbarItemKind { Command, Header, Separator }

/// <summary>One toolbar item: its kind and its natural width (including margins).</summary>
public readonly record struct ToolbarItemInfo(ToolbarItemKind Kind, double Width);

/// <summary>Result of <see cref="ToolbarOverflowPlanner.Plan"/>: which items stay on the bar, and whether the overflow button is needed.</summary>
public sealed record ToolbarOverflowPlan(IReadOnlyList<bool> Visible, bool HasOverflow);

/// <summary>
/// Pure single-row overflow decision for the Systems action bar — no UI types, no hardcoded window widths.
/// Items are kept as a PREFIX in their original order: the first command that does not fit, and everything after
/// it, goes to the overflow dropdown, so order is preserved and growing the window restores commands from the
/// start of the hidden tail. Section headers and separators never dangle: a header is shown only if a command of
/// its section follows it on the bar, a separator only between two visible commands.
/// </summary>
public static class ToolbarOverflowPlanner
{
    /// <param name="items">All items in toolbar order.</param>
    /// <param name="availableWidth">Width of the row.</param>
    /// <param name="spacing">Gap between neighbouring items (and before the overflow button).</param>
    /// <param name="overflowButtonWidth">Width reserved for the overflow button when it is shown.</param>
    public static ToolbarOverflowPlan Plan(
        IReadOnlyList<ToolbarItemInfo> items, double availableWidth, double spacing, double overflowButtonWidth)
    {
        int n = items.Count;
        var visible = new bool[n];
        if (n == 0) return new ToolbarOverflowPlan(visible, false);

        double total = 0;
        for (int i = 0; i < n; i++) total += items[i].Width + (i > 0 ? spacing : 0);

        if (total <= availableWidth + 0.01)
        {
            Array.Fill(visible, true);
            return new ToolbarOverflowPlan(visible, false);
        }

        // Overflow needed: reserve room for the chevron button, then take the longest prefix that fits.
        double budget = availableWidth - (overflowButtonWidth + spacing);
        double used = 0;
        int cut = 0;
        for (; cut < n; cut++)
        {
            double add = items[cut].Width + (cut > 0 ? spacing : 0);
            if (used + add > budget) break;
            used += add;
        }
        for (int i = 0; i < cut; i++) visible[i] = true;

        // Never end the bar on a header or separator.
        for (int i = cut - 1; i >= 0 && items[i].Kind != ToolbarItemKind.Command; i--) visible[i] = false;

        // Separators need a visible command before them (and so never follow another separator or lead the bar).
        int lastVisible = -1;
        for (int i = 0; i < n; i++)
        {
            if (!visible[i]) continue;
            if (items[i].Kind == ToolbarItemKind.Separator &&
                (lastVisible < 0 || items[lastVisible].Kind != ToolbarItemKind.Command))
            {
                visible[i] = false;
                continue;
            }
            lastVisible = i;
        }

        // Headers need a command right after them.
        for (int i = 0; i < n; i++)
        {
            if (!visible[i] || items[i].Kind != ToolbarItemKind.Header) continue;
            int next = i + 1;
            while (next < n && !visible[next]) next++;
            if (next >= n || items[next].Kind != ToolbarItemKind.Command) visible[i] = false;
        }

        return new ToolbarOverflowPlan(visible, true);
    }
}
