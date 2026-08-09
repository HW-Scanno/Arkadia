using System;

namespace Arkadia.Systems;

public sealed class SystemPlatform
{
    public required string Id           { get; init; }  // used as image filename, e.g. "nes"
    public required string Name         { get; init; }
    public required string Manufacturer { get; init; }
    public required string HardwareType { get; init; }
    public required int    DatLines     { get; init; }
    public required int    TotalTitles  { get; init; }
    public required int    Present      { get; init; }
    public required int    Outdated     { get; init; }
    public required int    Missing      { get; init; }
    public required int    Lost         { get; init; }

    /// <summary>
    /// Releases explicitly vetoed by the curator (status = 'unwanted').
    /// Excluded from the wanted-coverage denominator; reported separately as a share.
    /// </summary>
    public int Unwanted { get; init; }

    /// <summary>
    /// True when this platform has Group-DAT leaves whose status counts are not yet loaded (they are loaded
    /// lazily off the UI thread). While pending, the present/unwanted figures exclude those leaves, so the
    /// coverage shown must be a loading indicator — never a (false) partial percentage.
    /// </summary>
    public bool CoveragePending { get; init; }

    /// <summary>
    /// Releases we intend to keep = all titles minus the unwanted curator veto.
    /// Coverage answers "of the releases I want to keep, how complete is this system?",
    /// so the denominator must exclude unwanted (not merely-hidden catalog rows).
    /// </summary>
    public int WantedTitles => Math.Max(0, TotalTitles - Unwanted);

    /// <summary>
    /// Present releases are inherently wanted — 'unwanted' and 'present' are mutually
    /// exclusive status values, so the present count never includes unwanted rows.
    /// </summary>
    public int PresentWanted => Present;

    /// <summary>
    /// Wanted coverage percent, or null when there are no wanted releases.
    /// Integer division mirrors the previous coverage formula exactly when Unwanted = 0.
    /// Null is surfaced as "N/A" so an all-unwanted system does not read as 0% missing.
    /// </summary>
    public int? WantedCoveragePercent =>
        WantedTitles > 0 ? PresentWanted * 100 / WantedTitles : (int?)null;

    /// <summary>Wanted coverage as a display string; "N/A" when there are no wanted releases.</summary>
    public string WantedCoverage =>
        WantedCoveragePercent is { } pct ? $"{pct}%" : "N/A";

    /// <summary>Curation/exclusion ratio: unwanted over ALL releases (not the wanted subset).</summary>
    public int UnwantedSharePercent =>
        TotalTitles > 0 ? Unwanted * 100 / TotalTitles : 0;

    /// <summary>Unwanted share as a display string; "—" when the system has no releases.</summary>
    public string UnwantedShare =>
        TotalTitles > 0 ? $"{UnwantedSharePercent}%" : "—";
}
