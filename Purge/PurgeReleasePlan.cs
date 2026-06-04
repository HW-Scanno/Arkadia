using System.Collections.Generic;

namespace Arkadia.Purge;

/// <summary>
/// Describes a single local archive artifact that Purge will delete.
/// </summary>
public sealed record PurgeLocalArtifact(
    string DerivedArtifactId,
    string FileName,
    string AbsolutePath,
    long   Bytes,
    bool   FileExists
);

/// <summary>
/// Describes a single volume artifact (copy on a volume/disk) that Purge will delete.
/// AbsolutePath is null when the volume's disk is not currently mounted.
/// </summary>
public sealed record PurgeVolumeArtifact(
    string  VolumeArtifactId,
    string  VolumeId,
    string  VolumeLabel,
    string  DerivedArtifactId,
    string  DatLineId,
    string  FileName,
    string? AbsolutePath,
    string  DiskId,
    string  DiskLabel,
    bool    DiskMounted,
    long    Bytes
);

/// <summary>
/// Dry-run output from <see cref="PurgeReleasePlanner"/>.
/// Describes everything Purge will do without performing any action.
/// </summary>
public sealed class PurgeReleasePlan
{
    public string ReleaseId     { get; init; } = "";
    public string ReleaseName   { get; init; } = "";
    public string CurrentStatus { get; init; } = "";
    public string DatLineId     { get; init; } = "";
    public string DbPath        { get; init; } = "";

    public IReadOnlyList<PurgeLocalArtifact>  LocalArtifacts  { get; init; } = [];
    public IReadOnlyList<PurgeVolumeArtifact> VolumeArtifacts { get; init; } = [];

    public long TotalLocalBytes  { get; init; }
    public long TotalVolumeBytes { get; init; }

    /// <summary>All disk labels that must be mounted to execute this plan.</summary>
    public IReadOnlyList<string> RequiredDiskLabels { get; init; } = [];

    /// <summary>Disk labels that are required but currently offline.</summary>
    public IReadOnlyList<string> OfflineDiskLabels { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Issues   { get; init; } = [];

    /// <summary>
    /// True when all required disks are mounted and no blocking issues exist.
    /// The Execute button should be disabled when this is false.
    /// </summary>
    public bool CanExecute { get; init; }
}
