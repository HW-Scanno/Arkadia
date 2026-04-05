using System;

namespace Arkadia.Data;

public sealed class DiskRecord
{
    public required string Id                    { get; init; }
    public required string Label                 { get; init; }
    public required string Status                { get; init; }  // available | assigned | lost
    public required long   DeclaredCapacityBytes { get; init; }
    public          string Filesystem            { get; init; } = "";
    public          string Brand                 { get; init; } = "";
    public          string Model                 { get; init; } = "";
    public          string Serial                { get; init; } = "";
    public required DateTime CreatedAt           { get; init; }
    public required DateTime UpdatedAt           { get; init; }
}
