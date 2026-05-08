using System.Collections.Generic;
using System.Linq;

namespace Arkadia;

internal sealed record MediaGroupHeaderVm(string MediaType, int Count)
{
    public string Label => MediaType.Replace("-", " ").ToUpperInvariant() + " · " + Count;
}

internal static class CatalogMediaListHelpers
{
    /// <summary>
    /// Builds a display list that interleaves <see cref="MediaGroupHeaderVm"/> entries before
    /// each group of <see cref="MediaAssetVm"/> items. Input order is preserved within each group.
    /// </summary>
    internal static IReadOnlyList<object> BuildGroupedDisplay(IReadOnlyList<MediaAssetVm> vms)
    {
        if (vms.Count == 0) return [];
        var result = new List<object>(vms.Count + 8);
        foreach (var g in vms.GroupBy(v => v.Asset.MediaType))
        {
            result.Add(new MediaGroupHeaderVm(g.Key, g.Count()));
            result.AddRange(g);
        }
        return result;
    }
}
