namespace Arkadia.Data;

/// <summary>Per-release outcome produced by <see cref="VolumePlanner"/>.</summary>
public sealed class PlanningDecision
{
    public required string ReleaseId    { get; init; }
    public required string ReleaseName  { get; init; }
    public required long   TotalSizeBytes { get; init; }
    public required int    DerivedCount { get; init; }
    /// <summary>"include" | "skip" | "defer"</summary>
    public required string Decision     { get; init; }
    public required string Reason       { get; init; }
}
