namespace Arkadia;

/// <summary>
/// View-model row for PlanVolumeDialog. Wraps PlanningDecision with pre-formatted display values.
/// </summary>
public sealed class PlanningDisplayRow
{
    public required string Decision     { get; init; }
    public required string ReleaseName  { get; init; }
    public required int    DerivedCount { get; init; }
    public required string SizeLabel    { get; init; }
    public required string Reason       { get; init; }
}
