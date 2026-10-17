using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Activities;

namespace Arkadia.Ingestion;

/// <summary>The only decision a paused Group Verify offers on a leaf failure — Retry (rerun the SAME leaf) or Stop.</summary>
public enum GroupVerifyDecision { Retry, Stop }

/// <summary>Outcome of one leaf verification: success, or failure with a human reason (typically an exception message).</summary>
public readonly record struct GroupVerifyLeafOutcome(bool Success, string? FailureReason)
{
    public static GroupVerifyLeafOutcome Ok()                 => new(true, null);
    public static GroupVerifyLeafOutcome Fail(string reason)  => new(false, reason);
}

/// <summary>Context handed to the failure decider (and the UI modal) when a leaf fails. 1-based <see cref="Position"/>.</summary>
public sealed record GroupVerifyFailure(string LeafId, int Position, int Total, int CompletedBefore, string Reason);

/// <summary>Progress tick before/after each leaf. 1-based <see cref="Position"/>; <see cref="Completed"/> is the running success count.</summary>
public sealed record GroupVerifyTick(string LeafId, int Position, int Total, int Completed);

/// <summary>Final result of a Group Verify run.</summary>
public sealed record GroupVerifyRunResult(int Completed, int Total, int NotRun, bool Stopped, string? FailedLeafId, string? FailureReason);

/// <summary>
/// Pure, UI-free sequential Group-Verify state machine (MAX CONCURRENCY = 1). It runs each leaf's verifier one at a
/// time, in the given deterministic order; on failure it does NOT advance — it asks the decider for Retry (rerun the
/// SAME leaf from the beginning, no index change, previous successes never rerun) or Stop (terminate; already-verified
/// leaves stay done, the failed and later leaves are Not Run). There is NO Skip. The actual leaf verification and the
/// failure modal are injected as delegates, so the whole sequence/retry/stop logic is unit-testable without any UI,
/// filesystem, or DB.
/// </summary>
public static class GroupVerifyOrchestrator
{
    public static async Task<GroupVerifyRunResult> RunAsync(
        IReadOnlyList<string>                                          leafIds,
        Func<string, int, CancellationToken, Task<GroupVerifyLeafOutcome>> verifyLeaf,
        Func<GroupVerifyFailure, Task<GroupVerifyDecision>>            decideOnFailure,
        IProgress<GroupVerifyTick>?                                    progress = null,
        CancellationToken                                             ct       = default,
        ActivityMonitor?                                              activities = null)
    {
        // One VERIFYING scope for the whole run (no READY between leaves). It is released while the run is paused
        // waiting for the Retry/Stop decision — nothing is being verified then — and re-opened on Retry.
        IActivityScope? scope = activities?.Begin(ActivityKind.Verifying, "Group verify");
        try
        {
        int total     = leafIds.Count;
        int index     = 0;   // 0-based index of the leaf currently being verified
        int completed = 0;   // number of leaves that have PASSED

        while (index < total)
        {
            ct.ThrowIfCancellationRequested();
            var leafId = leafIds[index];
            progress?.Report(new GroupVerifyTick(leafId, index + 1, total, completed));

            var outcome = await verifyLeaf(leafId, index, ct);

            if (outcome.Success)
            {
                completed++;
                index++;
                progress?.Report(new GroupVerifyTick(leafId, index, total, completed));
                continue;
            }

            // Failure: pause, do NOT advance the index.
            scope?.Dispose(); scope = null;                                   // waiting for the user: no work in progress
            var decision = await decideOnFailure(
                new GroupVerifyFailure(leafId, index + 1, total, completed, outcome.FailureReason ?? ""));

            if (decision == GroupVerifyDecision.Stop)
                // Completed passed; this leaf FAILED (reported via FailedLeafId); the rest were never attempted.
                return new GroupVerifyRunResult(
                    Completed: completed, Total: total, NotRun: total - completed - 1,
                    Stopped: true, FailedLeafId: leafId, FailureReason: outcome.FailureReason);

            // Retry → loop with the SAME index (re-run this leaf from the beginning).
            scope = activities?.Begin(ActivityKind.Verifying, "Group verify");   // work resumes
        }

        return new GroupVerifyRunResult(
            Completed: completed, Total: total, NotRun: 0, Stopped: false, FailedLeafId: null, FailureReason: null);
        }
        finally { scope?.Dispose(); }
    }
}
