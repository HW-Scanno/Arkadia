using System.Collections.Generic;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>
/// The matchable identity of one DAT-line ("leaf") for an ingestion run: its SHA1/MD5 hash indexes, the
/// non-outdated releases, DAT-declared expected sizes, and any complete-but-not-yet-finalized (resumable)
/// staging releases. Built purely from already-loaded catalog data (no filesystem, no DB calls here) so a
/// future Group orchestration can build one per in-scope leaf and union the indexes.
///
/// <para>DB-authoritative satisfaction (present + ≥1 derived artifact) is intentionally NOT baked in here —
/// it is evaluated lazily by the caller (via <see cref="RedundantIncomingPolicy"/>) exactly as today, so
/// this type does not force per-release DB probes and stays reusable/aggregable.</para>
/// </summary>
public sealed class LeafIngestionNeeds
{
    /// <summary>Owning dat_line id — the authoritative leaf identity carried into each match target.</summary>
    public required string DatLineId { get; init; }

    /// <summary>Non-outdated releases keyed by id (outdated releases are excluded from matching, as today).</summary>
    public required IReadOnlyDictionary<string, ReleaseRecord> Releases { get; init; }

    /// <summary>SHA1 (lowercase hex) → one-or-more (releaseId, romName) targets. One hash may map to many.</summary>
    public required IReadOnlyDictionary<string, List<(string ReleaseId, string RomName)>> Sha1Index { get; init; }

    /// <summary>MD5 (lowercase hex) → one-or-more (releaseId, romName) targets (fallback when SHA1 misses).</summary>
    public required IReadOnlyDictionary<string, List<(string ReleaseId, string RomName)>> Md5Index { get; init; }

    /// <summary>"releaseId|romName" → DAT-declared expected size (only positive parseable sizes).</summary>
    public required IReadOnlyDictionary<string, long> ExpectedSizeIndex { get; init; }

    /// <summary>Wanted releases whose staging is already complete but have no derived artifact — to be finalized (Phase 7) even if no new incoming matches them this run.</summary>
    public required IReadOnlyList<string> ResumableReleaseIds { get; init; }

    /// <summary>
    /// Builds the needs from the leaf's releases + release files (verbatim of the historical index build:
    /// releases filtered to non-outdated; sha1/md5 indexes over their files; expected-size index over
    /// positive parseable sizes). <paramref name="resumableReleaseIds"/> comes from the existing
    /// <see cref="ResumableStagingDetector"/> result.
    /// </summary>
    public static LeafIngestionNeeds Build(
        string                                              datLineId,
        IReadOnlyList<ReleaseRecord>                        allReleases,
        IReadOnlyDictionary<string, List<ReleaseFileRecord>> allReleaseFiles,
        IReadOnlyList<string>                               resumableReleaseIds)
    {
        // Non-outdated releases (matches the historical `allReleasesList.Where(r => r.Status != "outdated")`).
        var releases = new Dictionary<string, ReleaseRecord>(System.StringComparer.Ordinal);
        foreach (var r in allReleases)
            if (r.Status != "outdated")
                releases[r.Id] = r;

        var sha1Index = new Dictionary<string, List<(string ReleaseId, string RomName)>>(System.StringComparer.OrdinalIgnoreCase);
        var md5Index  = new Dictionary<string, List<(string ReleaseId, string RomName)>>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var (releaseId, files) in allReleaseFiles)
        {
            if (!releases.ContainsKey(releaseId)) continue;
            foreach (var f in files)
            {
                if (f.Sha1.Length > 0)
                {
                    if (!sha1Index.TryGetValue(f.Sha1, out var sl)) { sl = new(); sha1Index[f.Sha1] = sl; }
                    sl.Add((releaseId, f.RomName));
                }
                if (f.Md5.Length > 0)
                {
                    if (!md5Index.TryGetValue(f.Md5, out var ml)) { ml = new(); md5Index[f.Md5] = ml; }
                    ml.Add((releaseId, f.RomName));
                }
            }
        }

        var expectedSizeIndex = new Dictionary<string, long>(System.StringComparer.Ordinal);
        foreach (var (rid, files) in allReleaseFiles)
            foreach (var f in files)
                if (long.TryParse(f.Size, out var sz) && sz > 0)
                    expectedSizeIndex[$"{rid}|{f.RomName}"] = sz;

        return new LeafIngestionNeeds
        {
            DatLineId           = datLineId,
            Releases            = releases,
            Sha1Index           = sha1Index,
            Md5Index            = md5Index,
            ExpectedSizeIndex   = expectedSizeIndex,
            ResumableReleaseIds = resumableReleaseIds,
        };
    }
}
