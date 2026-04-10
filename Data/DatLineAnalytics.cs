using System.Collections.Generic;

namespace Arkadia.Data;

/// <summary>
/// Per-DAT-line aggregate storage metrics collected in a single scan.
/// Returned by <see cref="DatLineStore.GetAnalyticsSummary"/> and aggregated
/// across all DAT lines by the Analytics view.
/// </summary>
public sealed record DatLineAnalyticsSummary(
    long                     TotalSourceBytes,
    long                     TotalDerivedBytes,
    Dictionary<string, long> DerivedByStrategy,
    Dictionary<string, int>  ExtensionCounts,
    int                      TotalDerivedCount);
