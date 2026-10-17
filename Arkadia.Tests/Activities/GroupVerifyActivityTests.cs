using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Activities;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Activities;

/// <summary>
/// GLOBAL-ACTIVITY-INTEGRATION-1 — Verify Group: ONE parent VERIFYING scope across all leaves (no READY between
/// them), released while the run is paused for Retry/Stop (nothing is being verified) and re-opened on Retry.
/// </summary>
public sealed class GroupVerifyActivityTests
{
    private static readonly IReadOnlyList<string> Leaves = new[] { "a", "b", "c" };

    [Fact]
    public async Task AllLeaves_RunUnderOneVerifyingScope_NoReadyBetweenLeaves()
    {
        var rec = new ActivityRecorder();
        var observedDuringLeaves = new List<ActivityKind?>();

        var result = await GroupVerifyOrchestrator.RunAsync(Leaves,
            (leaf, i, ct) => { observedDuringLeaves.Add(rec.Now); return Task.FromResult(GroupVerifyLeafOutcome.Ok()); },
            f => Task.FromResult(GroupVerifyDecision.Stop),
            activities: rec.Monitor);

        Assert.False(result.Stopped);
        Assert.All(observedDuringLeaves, k => Assert.Equal(ActivityKind.Verifying, k));
        Assert.Equal(3, observedDuringLeaves.Count);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Verifying, null }, rec.Seen);   // one period, then READY
        Assert.True(rec.Idle);
    }

    [Fact]
    public async Task PausedForRetryStop_ReleasesTheScope_AndRetryResumesIt()
    {
        var rec = new ActivityRecorder();
        int attempts = 0;
        ActivityKind? duringPrompt = ActivityKind.Loading;
        ActivityKind? afterRetry = null;

        var result = await GroupVerifyOrchestrator.RunAsync(Leaves,
            (leaf, i, ct) =>
            {
                if (leaf == "b" && attempts++ == 0) return Task.FromResult(GroupVerifyLeafOutcome.Fail("boom"));
                if (leaf == "b") afterRetry = rec.Now;
                return Task.FromResult(GroupVerifyLeafOutcome.Ok());
            },
            f => { duringPrompt = rec.Now; return Task.FromResult(GroupVerifyDecision.Retry); },
            activities: rec.Monitor);

        Assert.False(result.Stopped);
        Assert.Null(duringPrompt);                                          // waiting for the PO: READY, not "verifying"
        Assert.Equal(ActivityKind.Verifying, afterRetry);                   // work resumed
        Assert.Equal(new ActivityKind?[] { ActivityKind.Verifying, null, ActivityKind.Verifying, null }, rec.Seen);
        Assert.True(rec.Idle);
    }

    [Fact]
    public async Task Stop_LeavesNoScope()
    {
        var rec = new ActivityRecorder();
        var result = await GroupVerifyOrchestrator.RunAsync(Leaves,
            (leaf, i, ct) => Task.FromResult(leaf == "b" ? GroupVerifyLeafOutcome.Fail("x") : GroupVerifyLeafOutcome.Ok()),
            f => Task.FromResult(GroupVerifyDecision.Stop),
            activities: rec.Monitor);
        Assert.True(result.Stopped);
        Assert.True(rec.Idle);
        Assert.Null(rec.Seen.Last());
    }

    [Fact]
    public async Task ExceptionOrCancellation_ReleasesTheScope()
    {
        var rec = new ActivityRecorder();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GroupVerifyOrchestrator.RunAsync(Leaves,
            (leaf, i, ct) => throw new InvalidOperationException("boom"),
            f => Task.FromResult(GroupVerifyDecision.Stop), activities: rec.Monitor));
        Assert.True(rec.Idle);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GroupVerifyOrchestrator.RunAsync(Leaves,
            (leaf, i, ct) => Task.FromResult(GroupVerifyLeafOutcome.Ok()),
            f => Task.FromResult(GroupVerifyDecision.Stop), ct: cts.Token, activities: rec.Monitor));
        Assert.True(rec.Idle);

        // a decider that throws while the scope is already released must not leak or double-release anything
        await Assert.ThrowsAsync<InvalidOperationException>(() => GroupVerifyOrchestrator.RunAsync(Leaves,
            (leaf, i, ct) => Task.FromResult(GroupVerifyLeafOutcome.Fail("x")),
            f => throw new InvalidOperationException("dialog crashed"), activities: rec.Monitor));
        Assert.True(rec.Idle);
    }

    [Fact]
    public async Task Result_IsIdentical_WithAndWithoutTheMonitor()
    {
        async Task<(GroupVerifyRunResult R, List<string> Order)> Run(ActivityMonitor? m)
        {
            var order = new List<string>();
            int fails = 0;
            var r = await GroupVerifyOrchestrator.RunAsync(Leaves,
                (leaf, i, ct) => { order.Add(leaf); return Task.FromResult(leaf == "b" && fails++ < 1 ? GroupVerifyLeafOutcome.Fail("x") : GroupVerifyLeafOutcome.Ok()); },
                f => Task.FromResult(GroupVerifyDecision.Retry), activities: m);
            return (r, order);
        }
        var with    = await Run(new ActivityMonitor());
        var without = await Run(null);
        Assert.Equal(without.R, with.R);
        Assert.Equal(without.Order, with.Order);
    }

    [Fact]
    public async Task ALoadingScopeRunningAlongside_ComesBackAfterTheVerify_AndNeverReadyInBetween()
    {
        var rec = new ActivityRecorder();
        using var loading = rec.Monitor.Begin(ActivityKind.Loading);
        await GroupVerifyOrchestrator.RunAsync(Leaves,
            (leaf, i, ct) => Task.FromResult(GroupVerifyLeafOutcome.Ok()),
            f => Task.FromResult(GroupVerifyDecision.Stop), activities: rec.Monitor);
        Assert.DoesNotContain((ActivityKind?)null, rec.Seen);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, ActivityKind.Verifying, ActivityKind.Loading }, rec.Seen);
    }
}
