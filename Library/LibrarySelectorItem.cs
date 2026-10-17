using System;
using System.Collections.Generic;

namespace Arkadia.Library;

/// <summary>
/// A typed Library DAT-line selector entry. Display is deliberately decoupled from identity: the ComboBox
/// shows <see cref="Display"/> (the existing human label, plus the full authoritative DatLineId for Group
/// leaves so visually-identical labels stay distinguishable), while every selection/navigation path resolves
/// by <see cref="DatLineId"/> — never the label, the display string, or the list index. Single (non-group)
/// entries keep their historical label verbatim.
/// </summary>
public sealed record LibrarySelectorItem(string DatLineId, string Label, bool IsGroupLeaf, bool ShowId = false)
{
    /// <summary>
    /// Text shown in the ComboBox. Group leaves append the full DatLineId; a standalone label is unchanged unless
    /// it collides with another visible label (<see cref="ShowId"/>), in which case the id disambiguates it.
    /// </summary>
    public string Display => IsGroupLeaf || ShowId ? $"{Label} — {DatLineId}" : Label;

    // Avalonia renders an untemplated combo item via ToString().
    public override string ToString() => Display;

    /// <summary>
    /// Index of the item with the given authoritative <paramref name="datLineId"/> (Ordinal), or -1 if none.
    /// The single resolution primitive shared by all selector highlight/restore sites — identity is the id,
    /// never the label or position, so duplicate human labels remain independently selectable.
    /// </summary>
    public static int IndexOfDatLineId(IReadOnlyList<LibrarySelectorItem> items, string? datLineId)
    {
        if (string.IsNullOrEmpty(datLineId)) return -1;
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i].DatLineId, datLineId, StringComparison.Ordinal))
                return i;
        return -1;
    }
}
