using System.Collections.Generic;

namespace Arkadia.Volumes;

// ── Per-artifact entry action ─────────────────────────────────────────────────

public enum AppendEntryAction { Copy, Skip }

// ── Per-artifact plan entry ───────────────────────────────────────────────────

public sealed record AppendEntry
{
    public required string            DerivedArtifactId  { get; init; }
    public required string            ContentIdentityKey { get; init; }
    public required string            ReleaseName        { get; init; }
    public required string            FileName           { get; init; }
    public required long              SizeBytes          { get; init; }
    public required string            ExpectedSha1       { get; init; }
    public required string            ArchivePath        { get; init; }
    public required string            TargetPath         { get; init; }
    public required AppendEntryAction Action             { get; init; }
    public required string            Reason             { get; init; }
}

// ── Plan ──────────────────────────────────────────────────────────────────────

public sealed class AppendVolumePlan
{
    public required string VolumeId              { get; init; }
    public required string VolumeLabel           { get; init; }
    public required string DatLineId             { get; init; }
    public required string VolumeRootPath        { get; init; }
    public required long   TargetCapacityBytes   { get; init; }
    public required long   TargetUsedBytes       { get; init; }
    public required long   TargetFreeBytes       { get; init; }
    public required int    PlannedCount          { get; init; }
    public required long   PlannedBytes          { get; init; }
    public required int    SkippedCount          { get; init; }
    public required long   RemainingTargetFreeBytes { get; init; }
    public required long   TargetBytesAfter      { get; init; }
    // Diagnostic counters
    public required int    TotalCandidates        { get; init; }
    public required int    AlreadyAssignedSkipped { get; init; }
    public required int    ArchiveMissingSkipped  { get; init; }
    public required int    TargetCollisionSkipped { get; init; }
    public required int    TooLargeSkipped         { get; init; }
    public required int    InvalidHashSkipped      { get; init; }
    public required int    ReleaseUnwantedSkipped  { get; init; }
    public required IReadOnlyList<AppendEntry>       Entries          { get; init; }
    public required IReadOnlyDictionary<string, int> SkipReasonCounts { get; init; }
    public required bool   CanExecute            { get; init; }
}

// ── Live progress event ───────────────────────────────────────────────────────

public sealed record AppendVolumeProgress(string Action, string FileName, string Detail);

// ── Execution result ──────────────────────────────────────────────────────────

public sealed class AppendVolumeResult
{
    public int                   CopiedCount { get; set; }
    public long                  BytesCopied { get; set; }
    public int                   ErrorCount  { get; set; }
    public IReadOnlyList<string> LogLines    { get; init; } = [];
}
