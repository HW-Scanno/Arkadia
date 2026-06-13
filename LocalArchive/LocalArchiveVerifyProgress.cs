namespace Arkadia.LocalArchive;

/// <summary>
/// Live progress event emitted during LocalArchiveVerifyService.Verify/Repair.
/// Each event maps to one dialog row.
///
/// Scan actions (neutral — describe discovery/processing):
///   "archive-found-file"     — file enumerated in archive directory
///   "archive-hashing"        — SHA-1 computation in progress
///   "archive-wanted-ok"      — classified: WantedArchiveOk
///   "archive-unwanted-found" — classified: UnwantedArchiveArtifact
///   "archive-unknown-found"  — classified: UnknownArchiveFile
///   "archive-hash-mismatch"  — classified: ArchiveHashMismatch
///   "archive-collision"      — classified: ArchiveDuplicateCollision
///
/// Repair actions:
///   "archive-repair-moving"  — moving file to incoming-skip
///   "archive-repair-moved"   — file moved successfully
///   "archive-repair-skipped" — already absent, skipped
///   "archive-error"          — move or DB operation failed
/// </summary>
public sealed record LocalArchiveVerifyProgress(
    string Action,
    string FileName,
    string Detail
);
