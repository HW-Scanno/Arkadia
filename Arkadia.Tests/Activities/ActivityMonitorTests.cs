using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Activities;
using Xunit;

namespace Arkadia.Tests.Activities;

/// <summary>C64-ACTIVITY-1: the global activity registry — lifecycle, concurrency, priority, cleanup.</summary>
public sealed class ActivityMonitorTests
{
    private static (ActivityMonitor Monitor, List<ActivityKind?> Seen) Make()
    {
        var monitor = new ActivityMonitor();                       // synchronous delivery
        var seen = new List<ActivityKind?>();
        monitor.ForegroundChanged += k => seen.Add(k);
        return (monitor, seen);
    }

    // ── Begin / end ─────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_WhenNothingIsRegistered()
    {
        var (m, seen) = Make();
        Assert.Null(m.Foreground);
        Assert.True(m.IsIdle);
        Assert.Equal(0, m.ActiveCount);
        Assert.Empty(seen);
    }

    [Fact]
    public void BeginThenDispose_ReportsTheKind_ThenReturnsToReady()
    {
        var (m, seen) = Make();
        using (var a = m.Begin(ActivityKind.Verifying, "x"))
        {
            Assert.True(a.IsActive);
            Assert.Equal(ActivityKind.Verifying, a.Kind);
            Assert.Equal("x", a.Detail);
            Assert.Equal(ActivityKind.Verifying, m.Foreground);
        }
        Assert.Null(m.Foreground);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Verifying, null }, seen);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndNeverEndsAnotherScope()
    {
        var (m, seen) = Make();
        var a = m.Begin(ActivityKind.Loading);
        var b = m.Begin(ActivityKind.Loading);
        a.Dispose(); a.Dispose(); a.Dispose();
        Assert.False(a.IsActive);
        Assert.True(b.IsActive);
        Assert.Equal(1, m.ActiveCount);
        Assert.Equal(ActivityKind.Loading, m.Foreground);
        b.Dispose();
        Assert.True(m.IsIdle);
    }

    [Fact]
    public void ScopeIds_AreUnique_AndActiveSnapshotIsOrdered()
    {
        var (m, _) = Make();
        using var a = m.Begin(ActivityKind.Loading);
        using var b = m.Begin(ActivityKind.Moving);
        using var c = m.Begin(ActivityKind.Verifying);
        Assert.Equal(3, new[] { a.Id, b.Id, c.Id }.Distinct().Count());
        Assert.Equal(new[] { ActivityKind.Moving, ActivityKind.Verifying, ActivityKind.Loading }, m.Active.Select(x => x.Kind));
    }

    // ── The PO's overlap scenario ───────────────────────────────────────────────

    [Fact]
    public void RefreshThenVerify_VerifyEndsFirst_FallsBackToLoading_ThenReady()
    {
        var (m, seen) = Make();

        var refresh = m.Begin(ActivityKind.Loading, "Systems refresh");
        Assert.Equal(ActivityKind.Loading, m.Foreground);

        var verify = m.Begin(ActivityKind.Verifying);
        Assert.Equal(ActivityKind.Verifying, m.Foreground);

        verify.Dispose();                                           // verification ends while the refresh continues
        Assert.Equal(ActivityKind.Loading, m.Foreground);           // ← fallback, never READY
        Assert.False(m.IsIdle);

        refresh.Dispose();                                          // final System finished
        Assert.Null(m.Foreground);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, ActivityKind.Verifying, ActivityKind.Loading, null }, seen);
    }

    [Fact]
    public void CompletionOfOne_NeverCancelsTheOther_InEitherOrder()
    {
        foreach (var firstEnds in new[] { true, false })
        {
            var (m, _) = Make();
            var a = m.Begin(ActivityKind.Loading);
            var b = m.Begin(ActivityKind.Verifying);
            (firstEnds ? a : b).Dispose();
            Assert.False(m.IsIdle);
            Assert.Equal(firstEnds ? ActivityKind.Verifying : ActivityKind.Loading, m.Foreground);
            (firstEnds ? b : a).Dispose();
            Assert.True(m.IsIdle);
        }
    }

    [Fact]
    public void NeverReportsIdle_WhileAScopeIsStillAlive_AcrossManyInterleavings()
    {
        var (m, seen) = Make();
        var scopes = new List<IActivityScope>();
        var rng = new Random(12345);
        var kinds = Enum.GetValues<ActivityKind>();

        for (int i = 0; i < 2000; i++)
        {
            if (scopes.Count == 0 || rng.Next(3) != 0) scopes.Add(m.Begin(kinds[rng.Next(kinds.Length)]));
            else { var s = scopes[rng.Next(scopes.Count)]; s.Dispose(); scopes.Remove(s); }

            Assert.Equal(scopes.Count, m.ActiveCount);
            Assert.Equal(scopes.Count == 0, m.Foreground is null);   // idle ⇔ no live scope
            if (scopes.Count > 0)
                Assert.Equal(scopes.Max(s => ActivityCatalog.Priority(s.Kind)), ActivityCatalog.Priority(m.Foreground!.Value));
        }
        scopes.ForEach(s => s.Dispose());
        Assert.True(m.IsIdle);
        Assert.Null(seen.Last());
    }

    [Fact]
    public void Nested_SameKind_StaysActiveUntilTheLastScopeEnds()
    {
        var (m, seen) = Make();
        using var outer = m.Begin(ActivityKind.Verifying);
        var inner = m.Begin(ActivityKind.Verifying);
        inner.Dispose();
        Assert.Equal(ActivityKind.Verifying, m.Foreground);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Verifying }, seen);   // no spurious change notifications
    }

    // ── Priority policy ─────────────────────────────────────────────────────────

    [Fact]
    public void Priority_IsATotalOrder_OverEveryKind_WithLoadingLowest()
    {
        var all = Enum.GetValues<ActivityKind>();
        Assert.Equal(all.Length, ActivityCatalog.PriorityLowToHigh.Count);
        Assert.Equal(all.Length, ActivityCatalog.PriorityLowToHigh.Distinct().Count());
        Assert.Equal(all.Length, all.Select(ActivityCatalog.Priority).Distinct().Count());
        Assert.Equal(ActivityKind.Loading, ActivityCatalog.PriorityLowToHigh[0]);
        Assert.All(all, k => Assert.Contains(k, ActivityCatalog.PriorityLowToHigh));
    }

    [Theory]
    [InlineData(ActivityKind.Loading, ActivityKind.Verifying)]
    [InlineData(ActivityKind.Verifying, ActivityKind.Importing)]
    [InlineData(ActivityKind.Importing, ActivityKind.Ingesting)]
    [InlineData(ActivityKind.Ingesting, ActivityKind.Moving)]
    [InlineData(ActivityKind.Verifying, ActivityKind.Repairing)]
    [InlineData(ActivityKind.Scraping, ActivityKind.Verifying)]
    [InlineData(ActivityKind.Moving, ActivityKind.Restoring)]
    public void HigherPriority_WinsTheForeground_RegardlessOfStartOrder(ActivityKind low, ActivityKind high)
    {
        var (m1, _) = Make();
        using (m1.Begin(low)) using (m1.Begin(high)) Assert.Equal(high, m1.Foreground);
        var (m2, _) = Make();
        using (m2.Begin(high)) using (m2.Begin(low)) Assert.Equal(high, m2.Foreground);
    }

    [Fact]
    public void SameKind_TieBreak_OlderFirst_SoTheDisplayDoesNotFlip()
    {
        var (m, _) = Make();
        var first = m.Begin(ActivityKind.Moving, "first");
        var second = m.Begin(ActivityKind.Moving, "second");
        Assert.Equal("first", m.Active[0].Detail);
        first.Dispose();
        Assert.Equal("second", m.Active[0].Detail);
        Assert.Equal(ActivityKind.Moving, m.Foreground);
        second.Dispose();
    }

    [Fact]
    public void AfterTheForegroundEnds_TheNextBestStillActiveTakesOver()
    {
        var (m, seen) = Make();
        var loading = m.Begin(ActivityKind.Loading);
        var verifying = m.Begin(ActivityKind.Verifying);
        var moving = m.Begin(ActivityKind.Moving);

        Assert.Equal(ActivityKind.Moving, m.Foreground);
        moving.Dispose();    Assert.Equal(ActivityKind.Verifying, m.Foreground);
        verifying.Dispose(); Assert.Equal(ActivityKind.Loading, m.Foreground);
        loading.Dispose();   Assert.Null(m.Foreground);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, ActivityKind.Verifying, ActivityKind.Moving, ActivityKind.Verifying, ActivityKind.Loading, null }, seen);
    }

    // ── Exceptions, cancellation, helpers ───────────────────────────────────────

    [Fact]
    public void Exception_InsideUsing_StillEndsTheActivity()
    {
        var (m, _) = Make();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var a = m.Begin(ActivityKind.Importing);
            throw new InvalidOperationException("boom");
        }));
        Assert.True(m.IsIdle);
    }

    [Fact]
    public void Run_EndsTheActivity_OnSuccessAndOnFailure()
    {
        var (m, _) = Make();
        ActivityKind? during = null;
        m.Run(ActivityKind.Updating, () => during = m.Foreground);
        Assert.Equal(ActivityKind.Updating, during);
        Assert.True(m.IsIdle);

        Assert.Throws<ApplicationException>((Action)(() => m.Run(ActivityKind.Updating, () => throw new ApplicationException())));
        Assert.True(m.IsIdle);
    }

    [Fact]
    public async Task RunAsync_EndsTheActivity_OnSuccess_Failure_AndCancellation()
    {
        var (m, _) = Make();

        var v = await m.RunAsync(ActivityKind.Scraping, async () => { await Task.Yield(); return 42; });
        Assert.Equal(42, v);
        Assert.True(m.IsIdle);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            m.RunAsync(ActivityKind.Scraping, async () => { await Task.Yield(); throw new InvalidOperationException(); }));
        Assert.True(m.IsIdle);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            m.RunAsync(ActivityKind.Downloading, async () => { await Task.Delay(Timeout.Infinite, cts.Token); }));
        Assert.True(m.IsIdle);                                       // cancellation leaves no busy state
    }

    // ── Leak safety net ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BeginAndForget(ActivityMonitor m) => m.Begin(ActivityKind.Processing);   // handle never disposed

    [Fact]
    public void AForgottenHandle_DoesNotKeepTheConsoleBusyForever()
    {
        var (m, _) = Make();
        BeginAndForget(m);
        Assert.False(m.IsIdle);

        for (int i = 0; i < 10 && !m.IsIdle; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (!m.IsIdle) Thread.Sleep(20);
        }
        Assert.True(m.IsIdle);
    }

    [Fact]
    public void DisposedHandle_IsNotFinalizedTwice()
    {
        var (m, seen) = Make();
        var a = m.Begin(ActivityKind.Loading);
        a.Dispose();
        a = null!;
        GC.Collect(); GC.WaitForPendingFinalizers();
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, null }, seen);
    }

    // ── Threads and UI marshalling ──────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentBeginAndDispose_FromManyThreads_LeavesNoBusyState()
    {
        var m = new ActivityMonitor();
        var kinds = Enum.GetValues<ActivityKind>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(t => Task.Run(() =>
        {
            var rng = new Random(t);
            for (int i = 0; i < 500; i++)
            {
                using var a = m.Begin(kinds[rng.Next(kinds.Length)]);
                _ = m.Foreground;
            }
        })));
        Assert.True(m.IsIdle);
        Assert.Null(m.Foreground);
    }

    [Fact]
    public void Notifications_GoThroughThePostDelegate_AndReadTheCurrentStateWhenDelivered()
    {
        var queue = new Queue<Action>();
        var m = new ActivityMonitor(queue.Enqueue);                  // stands in for the UI dispatcher
        var seen = new List<ActivityKind?>();
        m.ForegroundChanged += seen.Add;

        var a = m.Begin(ActivityKind.Loading);
        var b = m.Begin(ActivityKind.Verifying);
        b.Dispose();
        Assert.Empty(seen);                                          // nothing is raised off the "UI thread"
        Assert.Equal(ActivityKind.Loading, m.Foreground);            // yet the truth is already queryable

        while (queue.Count > 0) queue.Dequeue()();                   // the UI thread catches up
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading }, seen);   // coalesced: no stale Verifying, no false idle

        a.Dispose();
        while (queue.Count > 0) queue.Dequeue()();
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, null }, seen);
    }

    [Fact]
    public void ABurstThatEndsWhereItStarted_RaisesNothing()
    {
        var queue = new Queue<Action>();
        var m = new ActivityMonitor(queue.Enqueue);
        var seen = new List<ActivityKind?>();
        m.ForegroundChanged += seen.Add;

        m.Begin(ActivityKind.Moving).Dispose();
        while (queue.Count > 0) queue.Dequeue()();
        Assert.Empty(seen);                                          // idle → idle: never a flash
    }

    // ── Catalog: text and pattern are separate from the kind ────────────────────

    [Fact]
    public void Labels_AreTheApprovedTexts()
    {
        Assert.Equal("LOADING...",    ActivityCatalog.Label(ActivityKind.Loading));
        Assert.Equal("MOVING...",     ActivityCatalog.Label(ActivityKind.Moving));
        Assert.Equal("INGESTING...",  ActivityCatalog.Label(ActivityKind.Ingesting));
        Assert.Equal("IMPORTING...",  ActivityCatalog.Label(ActivityKind.Importing));
        Assert.Equal("VERIFYING...",  ActivityCatalog.Label(ActivityKind.Verifying));
        foreach (var k in new[] { "APPENDING", "REPAIRING", "REABSORBING", "CREATING", "UPDATING", "SCRAPING",
                                  "DOWNLOADING", "EXPORTING", "RESTORING", "PROCESSING", "CLEANING", "PURGING" })
            Assert.Contains(k + "...", Enum.GetValues<ActivityKind>().Select(ActivityCatalog.Label));
        Assert.Equal("READY.", ActivityCatalog.ReadyLabel);
    }

    [Fact]
    public void EveryKind_HasAPattern_AndEveryPatternIsUsed()
    {
        var patterns = Enum.GetValues<ActivityKind>().Select(ActivityCatalog.PatternFor).ToList();
        Assert.Equal(Enum.GetValues<ActivityPattern>().OrderBy(p => p), patterns.Distinct().OrderBy(p => p));
        Assert.Equal(ActivityPattern.Raster,    ActivityCatalog.PatternFor(ActivityKind.Loading));
        Assert.Equal(ActivityPattern.BlockFlow, ActivityCatalog.PatternFor(ActivityKind.Moving));
        Assert.Equal(ActivityPattern.Columns,   ActivityCatalog.PatternFor(ActivityKind.Ingesting));
        Assert.Equal(ActivityPattern.HexStream, ActivityCatalog.PatternFor(ActivityKind.Importing));
        Assert.Equal(ActivityPattern.Scanner,   ActivityCatalog.PatternFor(ActivityKind.Verifying));
        Assert.Equal(ActivityPattern.Sweep,     ActivityCatalog.PatternFor(ActivityKind.Cleaning));
        Assert.Equal(ActivityPattern.Lava,      ActivityCatalog.PatternFor(ActivityKind.Purging));
    }

    [Fact]
    public void Monitor_DoesNotKnowAboutPatterns()
    {
        // workflows depend only on ActivityKind: the monitor source never mentions pattern/drawing concepts
        var src = System.IO.File.ReadAllText(RepoFile("Activities/ActivityMonitor.cs"));
        Assert.DoesNotContain("ActivityPattern", src);
        Assert.DoesNotContain("Render", src);
    }

    private static string RepoFile(string rel)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return System.IO.Path.Combine(dir!.FullName, rel);
    }
}
