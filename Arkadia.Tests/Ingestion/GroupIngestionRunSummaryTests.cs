using System;
using System.Collections.Generic;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Completion summary aggregation for the Group runtime bridge (M9C2B). Verifies neutral status wording and —
/// critically — that finalization/transform failures are reporting only and never alter the executor's
/// staging-based incoming disposition.
/// </summary>
public sealed class GroupIngestionRunSummaryTests
{
    private static (string, LeafFinalizationResult) Leaf(
        string id, string[]? success = null, string[]? incomplete = null, string[]? transformFailed = null)
        => (id, new LeafFinalizationResult
        {
            SuccessfulReleaseIds      = success         ?? Array.Empty<string>(),
            IncompleteReleaseIds      = incomplete      ?? Array.Empty<string>(),
            TransformFailedReleaseIds = transformFailed ?? Array.Empty<string>(),
        });

    [Fact] // clean run → COMPLETED
    public void Build_CleanRun_Completed()
    {
        var exec = new GroupIngestionExecutionResult
        {
            CandidatesScanned = 2, MatchedCandidates = 2,
            RequiredTargets = 2, SecuredTargets = 2, FailedTargets = 0,
            IncomingDeleted = 2,
        };
        var s = GroupIngestionRunSummary.Build(exec, new[] { Leaf("leaf-a", success: new[] { "r1" }) }, 1, 0);

        Assert.Equal(GroupRunStatus.Completed, s.Status);
        Assert.Equal("GROUP INGESTION COMPLETED", s.StatusTitle);
        Assert.Equal(1, s.SuccessfulReleases);
    }

    [Fact] // 20: transform failure is reported but does NOT change incoming deletion
    public void Build_TransformFailure_ReportedButDeletionUnchanged()
    {
        var exec = new GroupIngestionExecutionResult
        {
            CandidatesScanned = 1, MatchedCandidates = 1,
            RequiredTargets = 1, SecuredTargets = 1, FailedTargets = 0,
            IncomingDeleted = 1,   // staging-based: fully secured → deleted
        };
        var s = GroupIngestionRunSummary.Build(
            exec, new[] { Leaf("leaf-a", transformFailed: new[] { "r1" }) }, 0, 0);

        Assert.Equal(GroupRunStatus.CompletedWithWarnings, s.Status);
        Assert.Equal("COMPLETED WITH WARNINGS", s.StatusTitle);
        Assert.Equal(1, s.TransformFailedReleases);
        Assert.Equal(1, s.IncomingDeleted);   // unchanged by the transform failure
    }

    [Fact] // failed staging targets → warnings
    public void Build_FailedTargets_Warnings()
    {
        var exec = new GroupIngestionExecutionResult
        {
            RequiredTargets = 3, SecuredTargets = 2, FailedTargets = 1, IncomingKept = 1,
        };
        var s = GroupIngestionRunSummary.Build(exec, Array.Empty<(string, LeafFinalizationResult)>(), 0, 0);
        Assert.Equal(GroupRunStatus.CompletedWithWarnings, s.Status);
    }

    [Fact] // aborted run → ABORTED, reason carried
    public void Build_Aborted_StatusAborted()
    {
        var exec = new GroupIngestionExecutionResult { Aborted = true, AbortReason = "Insufficient disk space." };
        var s = GroupIngestionRunSummary.Build(exec, Array.Empty<(string, LeafFinalizationResult)>(), 0, 0);

        Assert.Equal(GroupRunStatus.Aborted, s.Status);
        Assert.Equal("GROUP INGESTION ABORTED", s.StatusTitle);
        Assert.Equal("Insufficient disk space.", s.AbortReason);
    }

    [Fact] // 19: leaf finalization results + errors captured; error rows produced
    public void Build_CapturesFinalizationResultsAndErrors()
    {
        var exec = new GroupIngestionExecutionResult
        {
            FinalizationErrorCount = 1, IncomingDeleted = 1,
        };
        exec.FinalizationErrors.Add(("leaf-b", "boom"));

        var s = GroupIngestionRunSummary.Build(exec, new[]
        {
            Leaf("leaf-a", success: new[] { "r1", "r2" }, incomplete: new[] { "r3" }),
        }, 0, 0);

        Assert.Equal(2, s.SuccessfulReleases);
        Assert.Equal(1, s.IncompleteReleases);
        Assert.Equal(1, s.LeafFinalizationErrors);
        Assert.Contains(s.ErrorDetails, o => o.Action == "leaf-finalization-error");
        Assert.Equal(GroupRunStatus.CompletedWithWarnings, s.Status);
    }

    [Fact] // micro-fix: top header counters = Group values (SKIPPED = IncomingSkipped), for the real Test-1 result
    public void TopCounters_Test1_ZeroMatchSkip()
    {
        var exec = new GroupIngestionExecutionResult
        {
            CandidatesScanned = 1, MatchedCandidates = 0, ZeroMatches = 1, IncomingSkipped = 1,
        };
        var s = GroupIngestionRunSummary.Build(exec, Array.Empty<(string, LeafFinalizationResult)>(), 0, 0);

        Assert.Equal((1, 0, 1), s.TopCounters());   // Processed=1, Matched=0, Skipped=1
    }

    [Fact] // non-zero matched: mapping is explicit (Processed=scanned, Matched=matched, Skipped=IncomingSkipped)
    public void TopCounters_MatchedAndSkipped()
    {
        var exec = new GroupIngestionExecutionResult
        {
            CandidatesScanned = 5, MatchedCandidates = 3, ZeroMatches = 2, IncomingSkipped = 2, IncomingDeleted = 3,
        };
        var s = GroupIngestionRunSummary.Build(exec, Array.Empty<(string, LeafFinalizationResult)>(), 0, 0);

        Assert.Equal((5, 3, 2), s.TopCounters());
    }

    [Fact] // SKIPPED reflects incoming disposition, not ZeroMatches (they can differ)
    public void TopCounters_SkippedIsIncomingSkipped_NotZeroMatches()
    {
        var exec = new GroupIngestionExecutionResult
        {
            CandidatesScanned = 4, MatchedCandidates = 1, ZeroMatches = 3, IncomingSkipped = 2, // 1 zero-match kept via error
        };
        var s = GroupIngestionRunSummary.Build(exec, Array.Empty<(string, LeafFinalizationResult)>(), 0, 0);

        Assert.Equal(2, s.TopCounters().Skipped);   // IncomingSkipped (2), not ZeroMatches (3)
    }

    [Fact] // container failure counts surface as warnings
    public void Build_FailedContainers_Warnings()
    {
        var exec = new GroupIngestionExecutionResult { CandidatesScanned = 0 };
        var s = GroupIngestionRunSummary.Build(exec, Array.Empty<(string, LeafFinalizationResult)>(), 1, 2);
        Assert.Equal(2, s.ContainersFailedOrSkipped);
        Assert.Equal(GroupRunStatus.CompletedWithWarnings, s.Status);
    }
}
