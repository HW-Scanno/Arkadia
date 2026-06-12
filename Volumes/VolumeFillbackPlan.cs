using System.Collections.Generic;

namespace Arkadia.Volumes;

// ── Operation mode ────────────────────────────────────────────────────────────

public enum FillbackOperationMode
{
    /// <summary>Source and target are on the same filesystem. Use File.Move.</summary>
    MoveSameDisk,
    /// <summary>Source and target are on different filesystems. Copy → Verify SHA1 → Delete source.</summary>
    CopyVerifyDeleteCrossDisk
}

// ── Per-artifact entry action ─────────────────────────────────────────────────

public enum FillbackEntryAction
{
    Move,
    CopyVerifyDelete,
    Skip,
    Error
}

// ── Per-artifact plan entry ───────────────────────────────────────────────────

public sealed record FillbackEntry
{
    public required string              VolumeArtifactId  { get; init; }
    public required string              DerivedArtifactId { get; init; }
    public required string              ReleaseName       { get; init; }
    public required string              ArtifactFileName  { get; init; }
    public required long                SizeBytes         { get; init; }
    public required string              ExpectedSha1      { get; init; }
    public required string              SourceFullPath    { get; init; }
    public required string              TargetFullPath    { get; init; }
    public required FillbackEntryAction Action            { get; init; }
    public required string              Reason            { get; init; }
}

// ── Plan ──────────────────────────────────────────────────────────────────────

public sealed class VolumeFillbackPlan
{
    public required string               SourceVolumeId           { get; init; }
    public required string               SourceVolumeLabel        { get; init; }
    public required string               SourceDiskLabel          { get; init; }
    public required string               SourceRootPath           { get; init; }
    public required string               TargetVolumeId           { get; init; }
    public required string               TargetVolumeLabel        { get; init; }
    public required string               TargetDiskLabel          { get; init; }
    public required string               TargetRootPath           { get; init; }
    public required FillbackOperationMode OperationMode           { get; init; }
    public required long                 TargetCapacityBytes      { get; init; }
    public required long                 TargetUsedBytes          { get; init; }
    public required long                 TargetFreeBytes          { get; init; }
    public required long                 PlannedBytes             { get; init; }
    public required int                  PlannedCount             { get; init; }
    public required int                  SkippedCount             { get; init; }
    public required long                 RemainingTargetFreeBytes { get; init; }
    public required long                 SourceBytesBefore        { get; init; }
    public required long                 SourceBytesAfter         { get; init; }
    public required long                 TargetBytesAfter         { get; init; }
    public required IReadOnlyList<FillbackEntry>           Entries          { get; init; }
    public required IReadOnlyList<string>                  Warnings         { get; init; }
    public required IReadOnlyList<string>                  Issues           { get; init; }
    /// <summary>
    /// Counts of skipped entries grouped by skip-reason code.
    /// Keys: SourceFileMissing, TooLargeForRemainingTargetSpace, AlreadyOnTarget, TargetCollision.
    /// </summary>
    public required IReadOnlyDictionary<string, int>       SkipReasonCounts { get; init; }
    public required bool                                   CanExecute       { get; init; }
}

// ── Live progress event ───────────────────────────────────────────────────────

/// <summary>
/// Live progress event emitted by <see cref="VolumeFillbackService"/>.
///
/// Action strings:
///   fillback-moving               — same-disk move started
///   fillback-copying              — cross-disk copy started
///   fillback-verifying            — SHA1 verification started
///   fillback-deleting-source      — cross-disk: deleting source after verified copy
///   fillback-moved                — same-disk move complete + verified
///   fillback-copied-verified-deleted — cross-disk: copy verified, source deleted
///   fillback-skip                 — entry skipped
///   fillback-error                — entry failed
///   usage-refreshed               — usage counters updated in DB
/// </summary>
public sealed record FillbackProgress(
    string Action,
    string FileName,
    string Detail
);

// ── Execution result ──────────────────────────────────────────────────────────

public sealed class VolumeFillbackResult
{
    public int                   MovedCount   { get; set; }
    public int                   CopiedCount  { get; set; }
    public long                  BytesMoved   { get; set; }
    public int                   ErrorCount   { get; set; }
    /// <summary>True when source volume has no remaining active artifacts after fillback.</summary>
    public bool                  SourceEmpty  { get; set; }
    public IReadOnlyList<string> LogLines     { get; init; } = [];
}
