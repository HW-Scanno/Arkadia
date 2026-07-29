using System;

namespace Arkadia.Data;

public sealed class VolumeRecord
{
    public required string    Id               { get; init; }
    public required string    Label            { get; init; }
    public required string    PlatformId       { get; init; }
    public required string    DatLineId        { get; init; }
    public required string    Status           { get; init; }  // init | present | lost
    public          string    Health           { get; init; } = "ok";  // ok | crit
    public required long      PlannedSizeBytes { get; init; }
    public required long      ActualSizeBytes  { get; init; }
    public required DateTime  CreatedAt        { get; init; }
    public          DateTime? VerifiedAt       { get; init; }
}
