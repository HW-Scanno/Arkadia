using System.Collections.Generic;

namespace Arkadia.Data;

/// <summary>Overall outcome returned by <see cref="VolumePlanner.Plan"/>.</summary>
public sealed class PlanningResult
{
    public required long                     VolumeCapacityBytes        { get; init; }
    public required long                     VolumeActualSizeBytes      { get; init; }
    public required long                     RemainingBytesBeforePlanning { get; init; }
    public required long                     PlannedBytes               { get; init; }
    public required long                     RemainingBytesAfterPlanning  { get; init; }
    public required IReadOnlyList<PlanningDecision> Items               { get; init; }
}
