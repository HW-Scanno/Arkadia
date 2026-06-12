namespace Arkadia.Volumes;

/// <summary>
/// Live progress event emitted during the hash / classification / recovery phases
/// of <see cref="VolumeVerifyService.Verify"/>.
///
/// Each event maps directly to one dialog row:
///   Action → Result column
///   Path   → Path column
///   Detail → Detail column
///
/// Neutral actions (do not increment verify counters):
///   "hashing"    — SHA1 computation has started for this file
///   "classified" — hash computed; classification determined
///
/// Result actions (reflect true verify/recovery outcome):
///   "verify-ok"               — artifact present at correct flat path, hash OK
///   "misplaced-found"         — artifact found at wrong path
///   "misplaced-restored"      — artifact moved back to flat root
///   "unwanted-found"          — unwanted artifact found in active area
///   "unwanted-moved"          — unwanted artifact moved to unwanted\
///   "known-unexpected-found"  — artifact from another volume found
///   "known-unexpected-moved"  — moved to known\&lt;volume label&gt;\
///   "unknown-found"           — unidentified file found in active area
///   "unknown-moved"           — unidentified file moved to unknown\
///   "missing"                 — expected artifact not found anywhere
///   "collision"               — move blocked by existing target file
/// </summary>
public sealed record VolumeVerifyProgress(
    string Action,
    string Path,
    string Detail
);
