using System.Collections.Generic;
using System.Linq;

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
    /// <summary>Human-readable skip reason; empty for Copy entries.</summary>
    public required string            Reason             { get; init; }
    /// <summary>Raw SkipReason key (e.g. "AlreadyAssigned") for filter/diagnostic use.</summary>
    public required string            ReasonKey          { get; init; }
}

// ── Plan ──────────────────────────────────────────────────────────────────────

public sealed class AppendVolumePlan
{
    // ── Volume context ─────────────────────────────────────────────────────────
    public required string VolumeId              { get; init; }
    public required string VolumeLabel           { get; init; }
    public required string DatLineId             { get; init; }
    public required string VolumeRootPath        { get; init; }
    public required long   TargetCapacityBytes   { get; init; }
    public required long   TargetUsedBytes       { get; init; }
    public required long   TargetFreeBytes       { get; init; }

    // ── Candidate pipeline counters ─────────────────────────────────────────────
    /// <summary>Total DA rows in this DAT line (wanted + unwanted).</summary>
    public required int    TotalDerivedArtifactsForDatLine { get; init; }
    /// <summary>DAs excluded because at least one linked release is unwanted.</summary>
    public required int    ReleaseUnwantedSkipped  { get; init; }
    /// <summary>Candidates that entered the per-file planning loop (= TotalDerivedArtifactsForDatLine − ReleaseUnwantedSkipped).</summary>
    public required int    TotalCandidates         { get; init; }
    public required int    AlreadyAssignedSkipped  { get; init; }
    public required int    ArchiveMissingSkipped   { get; init; }
    public required int    TargetCollisionSkipped  { get; init; }
    public required int    TooLargeSkipped         { get; init; }
    public required int    InvalidHashSkipped      { get; init; }
    public required int    ExcludedIncomingSkipPath  { get; init; }
    public required int    ExcludedZeroOrInvalidSize { get; init; }

    // ── Plan outcome ─────────────────────────────────────────────────────────────
    public required int    PlannedCount          { get; init; }
    public required long   PlannedBytes          { get; init; }
    public required int    SkippedCount          { get; init; }
    public required long   RemainingTargetFreeBytes { get; init; }
    public required long   TargetBytesAfter      { get; init; }

    // ── Candidate size diagnostics ────────────────────────────────────────────
    public required long   LargestCandidateBytes  { get; init; }
    public required long   SmallestCandidateBytes { get; init; }
    public required long   TotalCandidateBytes    { get; init; }

    // ── Archive physical diagnostics ─────────────────────────────────────────
    /// <summary>Physical files in archive\&lt;platformId&gt;\&lt;datLineId&gt;\</summary>
    public required int    ActiveArchivePhysicalFileCount         { get; init; }
    /// <summary>Wanted candidates that have a physical archive file.</summary>
    public required int    ActiveArchiveKnownWantedFileCount      { get; init; }
    /// <summary>Wanted candidates with a physical archive file that are not yet assigned.</summary>
    public required int    ActiveArchiveUnassignedWantedFileCount { get; init; }

    // ── Entries and skip reasons ──────────────────────────────────────────────
    public required IReadOnlyList<AppendEntry>                Entries          { get; init; }
    public required IReadOnlyDictionary<string, int>          SkipReasonCounts { get; init; }
    public required bool                                      CanExecute       { get; init; }

    // ── Computed ─────────────────────────────────────────────────────────────

    /// <summary>
    /// User-facing explanation for why PlannedCount == 0, or empty if files are planned.
    /// Derived from plan counters — no planner logic needed here.
    /// </summary>
    public string DominantReasonHint => ComputeHint();

    private string ComputeHint()
    {
        if (PlannedCount > 0) return "";

        int total = TotalDerivedArtifactsForDatLine;

        if (total == 0)
        {
            return ActiveArchivePhysicalFileCount > 0
                ? $"No DB artifacts found for this DAT line, but {ActiveArchivePhysicalFileCount} physical file(s) exist in the archive folder. Run Verify Archive to classify them."
                : "No archive artifacts found for this DAT line.";
        }

        if (ReleaseUnwantedSkipped > 0 && ReleaseUnwantedSkipped >= total)
            return $"No appendable files found. All {ReleaseUnwantedSkipped} archive artifact(s) are linked to releases marked UNWANTED.";

        if (AlreadyAssignedSkipped > 0 && AlreadyAssignedSkipped >= TotalCandidates && TotalCandidates > 0)
            return "No appendable files found. All archive artifacts for this DAT line are already assigned to active volumes.";

        if (ArchiveMissingSkipped > 0 && ArchiveMissingSkipped >= TotalCandidates && TotalCandidates > 0)
            return "No appendable files found. DB artifacts exist but their archive files are missing. Run Verify Archive.";

        if (TooLargeSkipped > 0 && TooLargeSkipped >= SkippedCount && SkippedCount > 0)
            return $"No appendable files found. The target has free space ({FormatBytes(TargetFreeBytes)}), but no remaining artifact fits within the remaining capacity.";

        if (TargetCollisionSkipped > 0 && TargetCollisionSkipped >= SkippedCount && SkippedCount > 0)
            return "No appendable files found. Target paths already exist on the volume. Run Verify Volume on the target.";

        if (ActiveArchivePhysicalFileCount > 0 && ActiveArchiveKnownWantedFileCount == 0)
            return $"No appendable files found. {ActiveArchivePhysicalFileCount} physical archive file(s) exist but none match wanted DB artifacts. Run Verify Archive.";

        if (SkipReasonCounts.Count > 0)
        {
            var dominant = SkipReasonCounts.MaxBy(kv => kv.Value);
            return $"No appendable files found. Dominant skip reason: {dominant.Key} ({dominant.Value} artifact(s)).";
        }

        return "No appendable files found.";
    }

    private static string FormatBytes(long b)
    {
        if (b <= 0)                   return "0 B";
        if (b < 1024L)                return $"{b} B";
        if (b < 1024L * 1024)         return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)  return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
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
