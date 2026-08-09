using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Systems;

/// <summary>One compact, read-only row in the Group Details dialog.</summary>
/// <param name="LeafId">dat_line id.</param>
/// <param name="SourcePath">The persisted <c>relative_dat_path</c> (original TOSEC-style source layout).</param>
/// <param name="MediaType">Media type display name (as currently persisted — never recalculated).</param>
/// <param name="Releases">Catalogued release count.</param>
public sealed record GroupDatDetailsRow(string LeafId, string SourcePath, string MediaType, int Releases);

/// <summary>Pure mapping from group leaves to Details rows. Uses only already-loaded data (no N+1, no I/O).</summary>
public static class GroupDatDetails
{
    /// <summary>
    /// Builds the Details rows from the single <see cref="CatalogService.GetLeavesForGroup"/> result. Source
    /// path is the persisted <c>relative_dat_path</c> (not an absolute SourcePath, not the leaf-DB
    /// DataStorePath); the media type is resolved from the supplied display-name map (persisted value, not
    /// recomputed).
    /// </summary>
    public static List<GroupDatDetailsRow> BuildRows(
        IEnumerable<GroupLeafRecord>            leaves,
        IReadOnlyDictionary<string, string>     mediaTypeNames)
        => leaves.Select(l => new GroupDatDetailsRow(
                LeafId:     l.DatLine.Id,
                SourcePath: l.GroupMetadata.RelativeDatPath ?? "",
                MediaType:  mediaTypeNames.TryGetValue(l.DatLine.MediaTypeId, out var name) ? name : l.DatLine.MediaTypeId,
                Releases:   l.DatLine.ReleaseCount))
            .ToList();
}
