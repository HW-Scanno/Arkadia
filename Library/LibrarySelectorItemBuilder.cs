using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Library;

/// <summary>
/// Pure builder of the typed DAT-line selector items for one platform (shared by the Library and Catalog tabs).
/// Identity is always <see cref="LibrarySelectorItem.DatLineId"/> (taken from the dataset's entries), never the
/// label. Group leaves are marked with their id only when <paramref name="markGroupLeaves"/> is true (Library);
/// the Catalog tab passes false so Group presentation is unchanged. Standalone DATs whose visible label collides
/// with another visible item get <see cref="LibrarySelectorItem.ShowId"/> so they stay distinguishable.
/// </summary>
public static class LibrarySelectorItemBuilder
{
    public static List<LibrarySelectorItem> Build(
        IEnumerable<LibraryDataset> datasets,
        string                      platform,
        ISet<string>                groupLeafIds,
        bool                        markGroupLeaves)
    {
        var raw = datasets
            .Where(d => d.Platform == platform)
            .Select(d =>
            {
                var id = d.Entries.Count > 0 ? d.Entries[0].DatLineId : "";
                return (Id: id, Label: d.DatLine, Leaf: groupLeafIds.Contains(id));
            })
            .ToList();

        // Items whose text is shown WITHOUT an id suffix: standalone ones, plus leaves when they are not marked.
        var collidingLabels = raw
            .Where(x => !(x.Leaf && markGroupLeaves))
            .GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return raw
            .Select(x => new LibrarySelectorItem(
                x.Id, x.Label,
                IsGroupLeaf: x.Leaf && markGroupLeaves,
                ShowId: !x.Leaf && collidingLabels.Contains(x.Label)))
            .ToList();
    }
}
