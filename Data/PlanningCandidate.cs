namespace Arkadia.Data;

/// <summary>
/// A release-grouped planning candidate returned by
/// <see cref="DatLineStore.GetPlanningCandidates"/>.
/// Read-only — not persisted.
/// </summary>
public sealed class PlanningCandidate
{
    public required string ReleaseId                  { get; init; }
    public required string ReleaseName                { get; init; }
    public required long   TotalSizeBytes             { get; init; }
    public required int    DerivedCount               { get; init; }
    /// <summary>True if ANY derived artifact of this release is already assigned to a volume.</summary>
    public required bool   IsAlreadyAssignedToAnyVolume { get; init; }
    /// <summary>True if ALL derived artifacts for this release are physically present in the archive folder.</summary>
    public required bool   IsCompleteInArchive        { get; init; }
}
