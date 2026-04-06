namespace Arkadia.Data;

/// <summary>
/// Runtime-only snapshot of a mounted volume, optionally decorated with Arkadia
/// marker data. Never persisted — all fields are observed at discovery time.
/// </summary>
public sealed class DiscoveredDisk
{
    public required string Mountpoint         { get; init; }
    public          string DiskId            { get; init; } = "";
    public          string DiskLabel         { get; init; } = "";   // from marker if present, else FS label
    public required long   TotalCapacityBytes { get; init; }
    public required long   FreeSpaceBytes     { get; init; }
    public          string FileSystemLabel    { get; init; } = "";  // raw OS volume label
    public          int    MarkerVersion      { get; init; }
    public          string InitializedUtc     { get; init; } = "";
    public          bool   HasMarker          => DiskId.Length > 0;
}
