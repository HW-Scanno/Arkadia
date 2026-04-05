using System;

namespace Arkadia.Data;

public sealed class VolumeLocationRecord
{
    public required string  Id           { get; init; }
    public required string  VolumeId     { get; init; }
    public required string  LocationType { get; init; }  // archive | disk | workspace
    public          string? DiskId       { get; init; }
    public          string? Path         { get; init; }
    public required bool    IsCurrent    { get; init; }
    public required DateTime CreatedAt   { get; init; }
}
