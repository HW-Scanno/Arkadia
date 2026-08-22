using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// The pure sequential Group-Verify state machine: deterministic one-at-a-time order, full success, failure with
/// Retry (rerun the SAME leaf, never the previous ones), repeated Retry, Stop (later leaves never run), no Skip,
/// and Retry re-invoking the verifier (fresh read). No UI/FS/DB.
/// </summary>
public sealed class GroupVerifyOrchestratorTests
{
    private static Task<GroupVerifyDecision> AlwaysStop(GroupVerifyFailure _)  => Task.FromResult(GroupVerifyDecision.Stop);
    private static Task<GroupVerifyDecision> AlwaysRetry(GroupVerifyFailure _) => Task.FromResult(GroupVerifyDecision.Retry);

    // ── 4 + 5: strictly sequential, full success ─────────────────────────────

    [Fact]
    public async Task Sequential_FullSuccess_InOrder_NoOverlap()
    {
        var calls   = new List<string>();
        int running = 0, maxConcurrent = 0;

        Task<GroupVerifyLeafOutcome> Verify(string id, int i, CancellationToken ct)
        {
            maxConcurrent = System.Math.Max(maxConcurrent, Interlocked.Increment(ref running));
            calls.Add(id);
            Interlocked.Decrement(ref running);
            return Task.FromResult(GroupVerifyLeafOutcome.Ok());
        }

        var r = await GroupVerifyOrchestrator.RunAsync(new[] { "A1", "A2", "A3" }, Verify, AlwaysStop);

        Assert.Equal(new[] { "A1", "A2", "A3" }, calls);   // deterministic order, A1<A2<A3
        Assert.Equal(1, maxConcurrent);                    // never parallel
        Assert.Equal(3, r.Completed);
        Assert.Equal(3, r.Total);
        Assert.Equal(0, r.NotRun);
        Assert.False(r.Stopped);
        Assert.Null(r.FailedLeafId);
    }

    // ── 3: scope — only the provided leaves are verified ─────────────────────

    [Fact]
    public async Task Scope_OnlyProvidedLeavesVerified()
    {
        var calls = new List<string>();
        Task<GroupVerifyLeafOutcome> Verify(string id, int i, CancellationToken ct)
        { calls.Add(id); return Task.FromResult(GroupVerifyLeafOutcome.Ok()); }

        await GroupVerifyOrchestrator.RunAsync(new[] { "A1", "A2", "A3" }, Verify, AlwaysStop);

        Assert.Equal(new[] { "A1", "A2", "A3" }, calls);
        Assert.DoesNotContain("B1", calls);
        Assert.DoesNotContain("C",  calls);
    }

    // ── 6: failure + Retry → rerun the SAME leaf; A1 never rerun ─────────────

    [Fact]
    public async Task FailureThenRetry_RerunsSameLeaf()
    {
        var calls = new List<string>();
        int a2 = 0;
        Task<GroupVerifyLeafOutcome> Verify(string id, int i, CancellationToken ct)
        {
            calls.Add(id);
            if (id == "A2") return Task.FromResult(++a2 == 1
                ? GroupVerifyLeafOutcome.Fail("boom") : GroupVerifyLeafOutcome.Ok());
            return Task.FromResult(GroupVerifyLeafOutcome.Ok());
        }

        var r = await GroupVerifyOrchestrator.RunAsync(new[] { "A1", "A2", "A3" }, Verify, AlwaysRetry);

        Assert.Equal(new[] { "A1", "A2", "A2", "A3" }, calls);   // A1 not rerun; A2 rerun once
        Assert.Equal(3, r.Completed);
        Assert.False(r.Stopped);
    }

    // ── 7: repeated Retry keeps the index on the failing leaf until success ──

    [Fact]
    public async Task RepeatedRetry_StaysOnLeafUntilSuccess()
    {
        var calls = new List<string>();
        int a2 = 0;
        Task<GroupVerifyLeafOutcome> Verify(string id, int i, CancellationToken ct)
        {
            calls.Add(id);
            if (id == "A2") return Task.FromResult(++a2 < 3
                ? GroupVerifyLeafOutcome.Fail("boom") : GroupVerifyLeafOutcome.Ok());
            return Task.FromResult(GroupVerifyLeafOutcome.Ok());
        }

        var r = await GroupVerifyOrchestrator.RunAsync(new[] { "A1", "A2", "A3" }, Verify, AlwaysRetry);

        Assert.Equal(new[] { "A1", "A2", "A2", "A2", "A3" }, calls);   // fails twice, passes third
        Assert.Equal(3, r.Completed);
    }

    // ── 8: failure + Stop → later leaves never run ───────────────────────────

    [Fact]
    public async Task FailureThenStop_LeavesLaterLeavesNotRun()
    {
        var calls = new List<string>();
        Task<GroupVerifyLeafOutcome> Verify(string id, int i, CancellationToken ct)
        {
            calls.Add(id);
            return Task.FromResult(id == "A2"
                ? GroupVerifyLeafOutcome.Fail("locked") : GroupVerifyLeafOutcome.Ok());
        }

        var r = await GroupVerifyOrchestrator.RunAsync(new[] { "A1", "A2", "A3" }, Verify, AlwaysStop);

        Assert.Equal(new[] { "A1", "A2" }, calls);      // A3 never invoked
        Assert.DoesNotContain("A3", calls);
        Assert.True(r.Stopped);
        Assert.Equal(1, r.Completed);                   // A1 completed
        Assert.Equal("A2", r.FailedLeafId);             // A2 failed
        Assert.Equal(1, r.NotRun);                      // A3 not run (excludes the failed leaf)
        Assert.Equal("locked", r.FailureReason);
    }

    // ── 9: the decision surface exposes ONLY Retry and Stop (no Skip) ────────

    [Fact]
    public void Decision_HasExactlyRetryAndStop()
        => Assert.Equal(new[] { "Retry", "Stop" }, System.Enum.GetNames(typeof(GroupVerifyDecision)));

    // ── 10: Retry re-invokes the verifier (a fresh attempt, not a cached result) ─

    [Fact]
    public async Task Retry_ReInvokesVerifier_FreshAttempt()
    {
        // The "external fix between attempts" is modeled as a flag the decider flips to make the retry succeed.
        bool fixedExternally = false;
        var calls = new List<string>();
        Task<GroupVerifyLeafOutcome> Verify(string id, int i, CancellationToken ct)
        {
            calls.Add(id);
            if (id == "A2" && !fixedExternally) return Task.FromResult(GroupVerifyLeafOutcome.Fail("missing file"));
            return Task.FromResult(GroupVerifyLeafOutcome.Ok());
        }
        Task<GroupVerifyDecision> Decide(GroupVerifyFailure f)
        {
            fixedExternally = true;   // PO restores the file while paused
            return Task.FromResult(GroupVerifyDecision.Retry);
        }

        var r = await GroupVerifyOrchestrator.RunAsync(new[] { "A1", "A2", "A3" }, Verify, Decide);

        Assert.Equal(new[] { "A1", "A2", "A2", "A3" }, calls);   // A2 re-read after the external fix
        Assert.Equal(3, r.Completed);
        Assert.False(r.Stopped);
    }
}
