namespace Arkadia.LocalArchive;

/// <summary>
/// Volume assignment context for a single derived artifact.
/// Passed to <see cref="LocalArchiveVerifyService.Verify"/> to enable
/// redundancy detection: if the archive copy is already safely stored on a
/// reachable volume, classify it as <see cref="LocalArchiveClass.RedundantArchiveCopy"/>.
/// </summary>
public sealed record AssignedVolumeInfo(
    string  VolumeId,
    string  VolumeLabel,
    string? VolumeRootPath);
