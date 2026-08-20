using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// The pure serialized Group-coverage state machine: FIFO order with selection priority, MAX CONCURRENCY = 1,
/// cache reuse, targeted invalidation, failure + retry, and the obsolete-result (generation) guard. No I/O.
/// </summary>
public sealed class GroupCoverageQueueTests
{
    private static readonly System.Func<string, bool> AllKnown = _ => true;
    private static GroupStorageFacts Facts(int present) => new(present, 0, present, 0);

    [Fact] // FIFO: enqueue a,b,c → begin returns a, then b, then c (one at a time)
    public void Fifo_OneAtATime()
    {
        var q = new GroupCoverageQueue();
        q.Enqueue("a", false, AllKnown);
        q.Enqueue("b", false, AllKnown);
        q.Enqueue("c", false, AllKnown);

        var first = q.Begin(AllKnown);
        Assert.Equal("a", first!.Value.GroupId);
        Assert.Null(q.Begin(AllKnown));            // MAX CONCURRENCY = 1 — nothing else starts
        Assert.Equal(1, q.InFlightCount);

        q.Complete("a", first.Value.Generation, Facts(1));
        Assert.Equal("b", q.Begin(AllKnown)!.Value.GroupId);
    }

    [Fact] // priority places/moves a group at the FRONT
    public void Priority_MovesToFront()
    {
        var q = new GroupCoverageQueue();
        q.Enqueue("a", false, AllKnown);
        q.Enqueue("b", false, AllKnown);
        q.Enqueue("c", priority: true, AllKnown);   // jumps ahead of a,b
        Assert.Equal("c", q.Begin(AllKnown)!.Value.GroupId);

        // Re-prioritising an already-queued group moves it to the front too.
        var q2 = new GroupCoverageQueue();
        q2.Enqueue("a", false, AllKnown);
        q2.Enqueue("b", false, AllKnown);
        q2.Enqueue("b", priority: true, AllKnown);
        Assert.Equal("b", q2.Begin(AllKnown)!.Value.GroupId);
    }

    [Fact] // dedup: enqueuing the same group twice queues it once
    public void Enqueue_DeduplicatesTight()
    {
        var q = new GroupCoverageQueue();
        q.Enqueue("a", false, AllKnown);
        q.Enqueue("a", false, AllKnown);
        Assert.Equal(1, q.QueuedCount);
    }

    [Fact] // cache reuse: a completed group is not re-queued and not re-begun
    public void Cached_NotRecomputed()
    {
        var q = new GroupCoverageQueue();
        q.Enqueue("a", false, AllKnown);
        var b = q.Begin(AllKnown)!;
        q.Complete("a", b.Value.Generation, Facts(5));

        Assert.True(q.IsCached("a"));
        Assert.True(q.TryGet("a", out var f)); Assert.Equal(5, f.Present);
        q.Enqueue("a", true, AllKnown);            // ignored — already cached
        Assert.Equal(0, q.QueuedCount);
        Assert.Null(q.Begin(AllKnown));
    }

    [Fact] // convergence: many pending groups all compute, never more than one in flight
    public void Convergence_AllComputed_MaxOneInFlight()
    {
        var q = new GroupCoverageQueue();
        var ids = new[] { "g1", "g2", "g3", "g4", "g5" };
        foreach (var id in ids) q.Enqueue(id, false, AllKnown);

        int guard = 0;
        while (q.Begin(AllKnown) is { } started)
        {
            Assert.Equal(1, q.InFlightCount);       // bound holds at every step
            q.Complete(started.GroupId, started.Generation, Facts(1));
            Assert.True(++guard <= 10);
        }
        foreach (var id in ids) Assert.True(q.IsCached(id));
    }

    [Fact] // invalidation: cached → invalidate → pending → recompute yields the fresh value
    public void Invalidate_MakesPending_ThenRecomputes()
    {
        var q = new GroupCoverageQueue();
        q.Enqueue("a", false, AllKnown);
        var b1 = q.Begin(AllKnown)!;
        q.Complete("a", b1.Value.Generation, Facts(0));   // stale: present 0

        q.Invalidate("a");
        Assert.False(q.IsCached("a"));
        q.Enqueue("a", false, AllKnown);                  // re-admitted
        var b2 = q.Begin(AllKnown)!;
        Assert.True(q.Complete("a", b2.Value.Generation, Facts(33)));
        Assert.True(q.TryGet("a", out var f)); Assert.Equal(33, f.Present);
    }

    [Fact] // failure: a faulted group is marked, not spinning, not auto-requeued; retry re-admits it once
    public void Failure_StaysFailed_UntilExplicitRetry()
    {
        var q = new GroupCoverageQueue();
        q.Enqueue("a", false, AllKnown);
        var b = q.Begin(AllKnown)!;
        Assert.True(q.Fail("a", b.Value.Generation));

        Assert.True(q.IsFailed("a"));
        q.Enqueue("a", false, AllKnown);        // NOT auto-requeued while failed
        Assert.Equal(0, q.QueuedCount);
        Assert.Null(q.Begin(AllKnown));

        q.Retry("a");                            // one explicit retry clears failed
        q.Enqueue("a", true, AllKnown);
        Assert.Equal("a", q.Begin(AllKnown)!.Value.GroupId);
    }

    [Fact] // obsolete result: a group invalidated MID-FLIGHT discards its stale result (generation drift)
    public void ObsoleteResult_Discarded()
    {
        var q = new GroupCoverageQueue();
        q.Enqueue("a", false, AllKnown);
        var started = q.Begin(AllKnown)!;         // captured generation

        q.Invalidate("a");                        // mutation lands while compute is in flight

        // The stale result must be rejected (Complete returns false, nothing cached).
        Assert.False(q.Complete("a", started.Value.Generation, Facts(999)));
        Assert.False(q.IsCached("a"));

        // A fresh compute at the new generation is accepted.
        q.Enqueue("a", false, AllKnown);
        var fresh = q.Begin(AllKnown)!;
        Assert.True(q.Complete("a", fresh.Value.Generation, Facts(7)));
        Assert.True(q.TryGet("a", out var f)); Assert.Equal(7, f.Present);
    }

    [Fact] // unknown groups (membership not loaded) are never enqueued/begun
    public void Unknown_NotEnqueued()
    {
        var q = new GroupCoverageQueue();
        System.Func<string, bool> known = id => id != "ghost";
        q.Enqueue("ghost", false, known);
        Assert.Equal(0, q.QueuedCount);
    }
}
