namespace Arkadia.LocalArchive;

/// <summary>
/// Live progress event emitted during LocalArchiveVerifyService.Verify/Repair.
/// Each event maps to one dialog row.
///
/// Scan actions (neutral — describe discovery/processing):
///   "archive-found-file"          — file enumerated in archive directory
///   "archive-hashing"             — SHA-1 computation in progress
///   "archive-wanted-ok"           — classified: WantedArchiveOk
///   "archive-unwanted-found"      — classified: UnwantedArchiveArtifact
///   "archive-unknown-found"       — classified: UnknownArchiveFile
///   "archive-hash-mismatch"       — classified: ArchiveHashMismatch
///   "archive-collision"           — classified: ArchiveDuplicateCollision
///   "archive-redundant-copy"      — classified: RedundantArchiveCopy (volume reachable)
///   "archive-volume-unavailable"  — classified: AssignedVolumeUnavailable (volume not reachable)
///
/// Repair actions:
///   "archive-repair-moving"        — moving file to incoming-skip/<platform>/
///   "archive-repair-moved"         — file moved successfully (unwanted/unknown/mismatch)
///   "archive-repair-skipped"       — already absent, skipped
///   "archive-error"                — move or DB operation failed
///   "archive-redundant-moved"      — redundant copy moved after volume re-verification
///   "archive-volume-copy-missing"  — volume copy gone or corrupt; archive kept in place
/// </summary>
public sealed record LocalArchiveVerifyProgress(
    string Action,
    string FileName,
    string Detail
);
