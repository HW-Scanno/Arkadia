using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Ingestion;

/// <summary>Overall Group run outcome for the completion banner.</summary>
public enum GroupRunStatus { Completed, CompletedWithWarnings, Aborted }

/// <summary>
/// Compact, testable completion summary for a Group ingestion run. Aggregates the executor result, the captured
/// per-leaf <see cref="LeafFinalizationResult"/>s, and pre-extraction container counts into a small set of
/// counters + an error/warning detail list. It never rolls back work; transform/finalization failures are
/// reporting only and do NOT affect the executor's incoming-cleanup decisions (which are staging-based).
/// </summary>
public sealed class GroupIngestionRunSummary
{
    // Candidates
    public int CandidatesScanned  { get; init; }
    public int MatchedCandidates  { get; init; }
    public int ZeroMatchSkipped   { get; init; }
    public int HashFailuresKept   { get; init; }
    // Staging (aggregate/authoritative)
    public int RequiredTargets    { get; init; }
    public int SecuredTargets     { get; init; }
    public int FailedTargets      { get; init; }
    // Finalization (captured from LeafFinalizationResult)
    public int SuccessfulReleases      { get; init; }
    public int IncompleteReleases      { get; init; }
    public int TransformFailedReleases { get; init; }
    public int LeafFinalizationErrors  { get; init; }
    // Incoming disposition
    public int IncomingDeleted { get; init; }
    public int IncomingKept    { get; init; }
    public int IncomingSkipped { get; init; }
    public int CleanupErrors   { get; init; }
    // Pre-extraction
    public int ContainersExtracted       { get; init; }
    public int ContainersFailedOrSkipped { get; init; }
    // Abort
    public bool    Aborted     { get; init; }
    public string? AbortReason { get; init; }

    public List<IngestionOperation> ErrorDetails { get; } = new();

    public bool HasWarnings =>
        FailedTargets > 0 || TransformFailedReleases > 0 || IncompleteReleases > 0 ||
        LeafFinalizationErrors > 0 || CleanupErrors > 0 || ContainersFailedOrSkipped > 0;

    public GroupRunStatus Status =>
        Aborted      ? GroupRunStatus.Aborted :
        HasWarnings  ? GroupRunStatus.CompletedWithWarnings :
                       GroupRunStatus.Completed;

    /// <summary>Neutral banner title — never claims success when warnings/errors occurred.</summary>
    public string StatusTitle => Status switch
    {
        GroupRunStatus.Completed             => "GROUP INGESTION COMPLETED",
        GroupRunStatus.CompletedWithWarnings => "COMPLETED WITH WARNINGS",
        _                                    => "GROUP INGESTION ABORTED",
    };

    /// <summary>
    /// The three top header counters (PROCESSED / MATCHED / SKIPPED) for a Group completion. Authoritative Group
    /// values — never the legacy Single/pre-ingestion result. SKIPPED = the actual Group incoming disposition
    /// count (<see cref="IncomingSkipped"/>), not <see cref="ZeroMatchSkipped"/>.
    /// </summary>
    public (int Processed, int Matched, int Skipped) TopCounters()
        => (CandidatesScanned, MatchedCandidates, IncomingSkipped);

    /// <summary>Compact label/value counters for the completion panel (no per-leaf noise).</summary>
    public IReadOnlyList<(string Label, string Value)> Counters() => new List<(string, string)>
    {
        ("Candidates scanned",        CandidatesScanned.ToString("N0")),
        ("Matched",                   MatchedCandidates.ToString("N0")),
        ("Zero-match skipped",        ZeroMatchSkipped.ToString("N0")),
        ("Hash failures kept",        HashFailuresKept.ToString("N0")),
        ("Required targets",          RequiredTargets.ToString("N0")),
        ("Secured targets",           SecuredTargets.ToString("N0")),
        ("Failed targets",            FailedTargets.ToString("N0")),
        ("Releases finalized",        SuccessfulReleases.ToString("N0")),
        ("Incomplete releases",       IncompleteReleases.ToString("N0")),
        ("Transform-failed releases", TransformFailedReleases.ToString("N0")),
        ("Leaf finalization errors",  LeafFinalizationErrors.ToString("N0")),
        ("Incoming deleted",          IncomingDeleted.ToString("N0")),
        ("Incoming kept",             IncomingKept.ToString("N0")),
        ("Incoming skipped",          IncomingSkipped.ToString("N0")),
        ("Cleanup errors",            CleanupErrors.ToString("N0")),
        ("Containers extracted",      ContainersExtracted.ToString("N0")),
        ("Containers failed/skipped", ContainersFailedOrSkipped.ToString("N0")),
    };

    public static GroupIngestionRunSummary Build(
        GroupIngestionExecutionResult                              exec,
        IEnumerable<(string DatLineId, LeafFinalizationResult R)>  finalizations,
        int                                                        containersExtracted,
        int                                                        containersFailedOrSkipped)
    {
        var fin = finalizations.ToList();

        var summary = new GroupIngestionRunSummary
        {
            CandidatesScanned = exec.CandidatesScanned,
            MatchedCandidates = exec.MatchedCandidates,
            ZeroMatchSkipped  = exec.ZeroMatches,
            HashFailuresKept  = exec.HashFailures,
            RequiredTargets   = exec.RequiredTargets,
            SecuredTargets    = exec.SecuredTargets,
            FailedTargets     = exec.FailedTargets,

            SuccessfulReleases      = fin.Sum(f => f.R.SuccessfulReleaseIds.Count),
            IncompleteReleases      = fin.Sum(f => f.R.IncompleteReleaseIds.Count),
            TransformFailedReleases = fin.Sum(f => f.R.TransformFailedReleaseIds.Count),
            LeafFinalizationErrors  = exec.FinalizationErrorCount,

            IncomingDeleted = exec.IncomingDeleted,
            IncomingKept    = exec.IncomingKept,
            IncomingSkipped = exec.IncomingSkipped,
            CleanupErrors   = exec.CleanupErrors,

            ContainersExtracted       = containersExtracted,
            ContainersFailedOrSkipped = containersFailedOrSkipped,

            Aborted     = exec.Aborted,
            AbortReason = exec.AbortReason,
        };

        // Error/warning detail rows only (never the successful leaves).
        foreach (var (datLineId, reason) in exec.FinalizationErrors)
            summary.ErrorDetails.Add(new IngestionOperation(datLineId, "leaf-finalization-error", reason));
        foreach (var op in exec.Operations)   // cleanup errors (skip/delete failures), recorded as text
            summary.ErrorDetails.Add(new IngestionOperation("cleanup", "cleanup-error", op));

        return summary;
    }
}
