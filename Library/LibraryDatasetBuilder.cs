using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Library;

/// <summary>
/// The outcome of a targeted single-leaf refresh: the dataset that was replaced and the one now in its place
/// (same list position, same platform, same DAT-line label, same leaf).
/// </summary>
public sealed record LibraryLeafRefresh(LibraryDataset Previous, LibraryDataset Current);

/// <summary>
/// Builds the in-memory Library datasets from the catalog rows and the per-DAT-line databases. UI-free.
///
/// <para><see cref="BuildLeaf"/> is the ONLY place that turns one leaf database into a <see cref="LibraryDataset"/>;
/// both the full build (<see cref="BuildAll"/>) and the targeted post-mutation refresh
/// (<see cref="TryRefreshLeaf"/>) go through it, so a refreshed leaf is identical by construction to what a full
/// rebuild would produce for that leaf.</para>
/// </summary>
public static class LibraryDatasetBuilder
{
    /// <summary>Full build: one dataset per qualifying DAT line, in the order of <paramref name="datLines"/>.</summary>
    public static List<LibraryDataset> BuildAll(
        IReadOnlyList<HardwareFamilyRecord> platforms,
        IReadOnlyList<DatLineRecord>        datLines,
        string                              dataDir,
        Func<DatLineRecord, string>         datLineLabel)
    {
        var merged = new List<LibraryDataset>();
        foreach (var dl in datLines)
        {
            var dataset = BuildLeaf(dl, ResolvePlatformName(platforms, dl), dataDir, datLineLabel);
            if (dataset is not null)
                merged.Add(dataset);
        }
        return merged;
    }

    /// <summary>Platform display name of a DAT line: the hardware family's name, or its id when the family is unknown.</summary>
    public static string ResolvePlatformName(IReadOnlyList<HardwareFamilyRecord> platforms, DatLineRecord dl)
        => platforms.FirstOrDefault(p => p.Id == dl.HardwareFamilyId)?.Name ?? dl.HardwareFamilyId;

    /// <summary>
    /// Loads ONE leaf database into a dataset. Returns null when the leaf contributes no dataset: not catalog-enabled,
    /// no data store path, database file missing, or no releases. Read-only for media: no folders are created
    /// (lazy directory creation).
    /// </summary>
    public static LibraryDataset? BuildLeaf(
        DatLineRecord               dl,
        string                      platformName,
        string                      dataDir,
        Func<DatLineRecord, string> datLineLabel)
    {
        if (!dl.CatalogEnabled) return null;
        if (dl.DataStorePath.Length == 0) return null;

        var absPath = Path.Combine(dataDir, dl.DataStorePath);
        if (!File.Exists(absPath)) return null;

        var store        = new DatLineStore(absPath);
        var filesByRelId = store.LoadAllReleaseFiles();
        var metadata     = store.LoadReleaseMetadata();
        var titleMode    = dl.LibraryTitleMode;

        var releases = store.LoadReleases()
            .Select(r =>
            {
                filesByRelId.TryGetValue(r.Id, out var romFiles);
                metadata.TryGetValue(r.Id, out var meta);
                return new LibraryEntry
                {
                    Name                  = r.Name,
                    DisplayName           = LibraryTitleResolver.Resolve(r.Name, titleMode, meta?.Title),
                    HardwareFamilyId      = dl.HardwareFamilyId,
                    Authority             = dl.Authority,
                    Metadata              = meta,
                    Platform              = platformName,
                    Status                = Capitalize(r.Status),
                    Region                = r.Region,
                    Languages             = r.Languages.ToUpperInvariant(),
                    Format                = r.Format,
                    Size                  = r.Size,
                    Tier                  = r.Tier,
                    RomFiles              = romFiles ?? [],
                    ReleaseId             = r.Id,
                    DbPath                = absPath,
                    DatLineId             = dl.Id,
                    TransformStrategyType = dl.TransformStrategyType,
                    IntroducedAtUtc       = r.IntroducedAtUtc,
                    ShowInCatalog         = r.ShowInCatalog,
                };
            })
            .ToList();

        return releases.Count > 0
            ? new LibraryDataset(platformName, datLineLabel(dl), releases)
            : null;
    }

    /// <summary>
    /// Targeted refresh after a mutation confined to ONE leaf database (e.g. a single release marked Unwanted or
    /// restored to Wanted): reloads that leaf only and swaps its dataset in place, leaving every other dataset —
    /// and the list order — untouched. No other leaf database is opened.
    ///
    /// <para>Returns null, changing nothing, whenever the swap is not provably equivalent to a full rebuild — the
    /// caller must then run the full rebuild: the DAT line is no longer in the catalog, it had no dataset before,
    /// it yields none now (disabled / database missing / no releases), or its platform or label changed (the
    /// Library selectors are derived from those and would need repopulating).</para>
    ///
    /// <para>Language flag bitmaps are a view decoration resolved after load, not leaf data; they are carried over
    /// per release when the release's languages are unchanged, so a refresh does not blank them.</para>
    /// </summary>
    public static LibraryLeafRefresh? TryRefreshLeaf(
        List<LibraryDataset>                datasets,
        IReadOnlyList<HardwareFamilyRecord> platforms,
        IReadOnlyList<DatLineRecord>        datLines,
        string                              datLineId,
        string                              dataDir,
        Func<DatLineRecord, string>         datLineLabel)
    {
        var dl = datLines.FirstOrDefault(d => string.Equals(d.Id, datLineId, StringComparison.Ordinal));
        if (dl is null) return null;

        // Same identity rule as LibraryDatasetSelector.ByDatLineId: a dataset belongs to its entries' DatLineId.
        var index = datasets.FindIndex(d => d.Entries.Count > 0
            && string.Equals(d.Entries[0].DatLineId, datLineId, StringComparison.Ordinal));
        if (index < 0) return null;

        var previous = datasets[index];
        var current  = BuildLeaf(dl, ResolvePlatformName(platforms, dl), dataDir, datLineLabel);
        if (current is null) return null;
        if (!string.Equals(current.Platform, previous.Platform, StringComparison.Ordinal)) return null;
        if (!string.Equals(current.DatLine,  previous.DatLine,  StringComparison.Ordinal)) return null;

        CarryOverFlagImages(previous, current);

        datasets[index] = current;
        return new LibraryLeafRefresh(previous, current);
    }

    private static void CarryOverFlagImages(LibraryDataset previous, LibraryDataset current)
    {
        if (!previous.Entries.Any(e => e.FlagImages.Count > 0)) return;   // nothing was resolved — nothing to keep

        var byReleaseId = new Dictionary<string, LibraryEntry>(previous.Entries.Count, StringComparer.Ordinal);
        foreach (var e in previous.Entries)
            byReleaseId[e.ReleaseId] = e;

        foreach (var e in current.Entries)
            if (byReleaseId.TryGetValue(e.ReleaseId, out var old)
                && string.Equals(old.Languages, e.Languages, StringComparison.Ordinal))
                e.FlagImages = old.FlagImages;
    }

    /// <summary>"present" → "Present" (the Library's human-readable status form).</summary>
    public static string Capitalize(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}
