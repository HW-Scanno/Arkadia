using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Library;

/// <summary>
/// Pure selection of a Library dataset by its authoritative dat_line id. The display label
/// (authority · media) collides across group leaves, so navigation must match on
/// <see cref="LibraryEntry.DatLineId"/> — never the label or a path. Returns null when no dataset owns that
/// id (caller must NOT fall back to another leaf).
/// </summary>
public static class LibraryDatasetSelector
{
    public static LibraryDataset? ByDatLineId(IEnumerable<LibraryDataset> datasets, string datLineId)
        => datasets.FirstOrDefault(d => d.Entries.Count > 0
            && string.Equals(d.Entries[0].DatLineId, datLineId, StringComparison.Ordinal));
}
