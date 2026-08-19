using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Systems;

/// <summary>One compact, read-only row in the Group Details dialog.</summary>
/// <param name="LeafId">dat_line id.</param>
/// <param name="SourcePath">The persisted <c>relative_dat_path</c> (original TOSEC-style source layout).</param>
/// <param name="MediaType">Media type display name (as currently persisted — never recalculated).</param>
/// <param name="Releases">Catalogued release count.</param>
/// <param name="Present">Present releases in this leaf (from its DB; 0 if the DB is absent).</param>
/// <param name="Unwanted">Unwanted (curator-vetoed) releases in this leaf.</param>
public sealed record GroupDatDetailsRow(
    string LeafId, string SourcePath, string MediaType, int Releases, int Present = 0, int Unwanted = 0)
{
    /// <summary>Wanted-coverage percent for this leaf (present ÷ max(0, releases − unwanted)); null ⇒ "N/A".</summary>
    public int? CompletionPercent => CompletionMath.Percent(Present, Releases, Unwanted);

    /// <summary>Completion as a display string; "N/A" when there are no wanted releases.</summary>
    public string CompletionText => CompletionMath.Text(CompletionPercent);

    /// <summary>
    /// True when this leaf is "missing" for the Details filter: it has wanted releases and is not yet complete.
    /// A leaf with no wanted releases (N/A) is never missing. Independent of storage lifecycle — a 97% ARCHIVED
    /// leaf is still missing.
    /// </summary>
    public bool IsIncomplete => CompletionMath.IsIncomplete(Present, Releases, Unwanted);
}

/// <summary>Pure mapping from group leaves to Details rows. Uses only already-loaded data (no N+1, no I/O).</summary>
public static class GroupDatDetails
{
    /// <summary>
    /// Builds the Details rows from the single <see cref="CatalogService.GetLeavesForGroup"/> result. Source
    /// path is the persisted <c>relative_dat_path</c> (not an absolute SourcePath, not the leaf-DB
    /// DataStorePath); the media type is resolved from the supplied display-name map (persisted value, not
    /// recomputed). Per-leaf completion (<paramref name="presentByLeaf"/> / <paramref name="unwantedByLeaf"/>) is
    /// supplied by the caller from its off-thread leaf-DB pass; a leaf absent from those maps stays at 0 (N/A).
    /// </summary>
    public static List<GroupDatDetailsRow> BuildRows(
        IEnumerable<GroupLeafRecord>            leaves,
        IReadOnlyDictionary<string, string>     mediaTypeNames,
        IReadOnlyDictionary<string, int>?       presentByLeaf  = null,
        IReadOnlyDictionary<string, int>?       unwantedByLeaf = null)
        => leaves.Select(l => new GroupDatDetailsRow(
                LeafId:     l.DatLine.Id,
                SourcePath: l.GroupMetadata.RelativeDatPath ?? "",
                MediaType:  mediaTypeNames.TryGetValue(l.DatLine.MediaTypeId, out var name) ? name : l.DatLine.MediaTypeId,
                Releases:   l.DatLine.ReleaseCount,
                Present:    presentByLeaf  is not null && presentByLeaf.TryGetValue(l.DatLine.Id, out var p)  ? p : 0,
                Unwanted:   unwantedByLeaf is not null && unwantedByLeaf.TryGetValue(l.DatLine.Id, out var u) ? u : 0))
            .ToList();
}
