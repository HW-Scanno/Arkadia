using System;

namespace Arkadia.Data;

public sealed class VolumeRecord
{
    public required string    Id               { get; init; }
    public required string    Label            { get; init; }
    public required string    PlatformId       { get; init; }
    /// <summary>Single-DAT owner. Exactly one of <see cref="DatLineId"/> / <see cref="GroupId"/> is set.</summary>
    public          string?   DatLineId        { get; init; }
    /// <summary>Group-DAT owner (references dat_groups.id). Exactly one of <see cref="DatLineId"/> / this is set.</summary>
    public          string?   GroupId          { get; init; }
    public required string    Status           { get; init; }  // init | present | lost
    public          string    Health           { get; init; } = "ok";  // ok | crit
    public required long      PlannedSizeBytes { get; init; }
    public required long      ActualSizeBytes  { get; init; }
    public required DateTime  CreatedAt        { get; init; }
    public          DateTime? VerifiedAt       { get; init; }

    public bool IsDatLineOwned => DatLineId is not null;
    public bool IsGroupOwned   => GroupId   is not null;
}
