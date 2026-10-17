using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Activities;
using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// C64-ACTIVITY-1: the real Systems loading lifecycle. <see cref="GroupCoverageDrainer"/> is the production worker
/// (MainWindow delegates to it); here it runs with controllable computes and a hand-pumped "UI thread" queue.
/// </summary>
public sealed class SystemsLoadingActivityTests
{
    private static readonly IReadOnlyList<(string, string, int)> NoLeaves = Array.Empty<(string, string, int)>();

    private sealed class Rig
    {
        public readonly GroupCoverageQueue Queue = new();
        public readonly ActivityMonitor Monitor = new();                          // synchronous notifications
        public readonly List<ActivityKind?> Seen = new();
        public readonly ConcurrentQueue<Action> Ui = new();                       // the "UI thread"
        public readonly Dictionary<string, TaskCompletionSource<GroupStorageFacts>> Computes = new();
        public readonly List<string> Started = new();
        public int Refreshes;
        public Action? OnRefresh;
        public GroupCoverageDrainer Drainer { get; }

        public Rig()
        {
            Monitor.ForegroundChanged += k => Seen.Add(k);
            string? current = null;
            Drainer = new GroupCoverageDrainer(
                Queue,
                _ => true,
                gid => { current = gid; return NoLeaves; },
                _ =>
                {
                    var tcs = new TaskCompletionSource<GroupStorageFacts>();
                    Computes[$"{current}#{Started.Count(s => s == current)}"] = tcs;
                    Started.Add(current!);
                    return tcs.Task;
                },
                Ui.Enqueue,
                () => { Refreshes++; OnRefresh?.Invoke(); },
                Monitor);
        }

        public void Enqueue(params string[] groups) { foreach (var g in groups) Queue.Enqueue(g, priority: false, _ => true); }

        /// <summary>Finishes the n-th compute of a group and lets the UI thread apply its continuation.</summary>
        public void Finish(string group, int nth = 0, bool fail = false)
        {
            var tcs = Computes[$"{group}#{nth}"];
            var before = Ui.Count;
            if (fail) tcs.SetException(new IOException("leaf DB unreadable")); else tcs.SetResult(new GroupStorageFacts(1, 0, 1, 0));
            Assert.True(SpinWait.SpinUntil(() => Ui.Count > before, TimeSpan.FromSeconds(30)), "continuation was not posted");
            while (Ui.TryDequeue(out var a)) a();
        }
    }

    // ── Basic lifecycle ─────────────────────────────────────────────────────────

    [Fact]
    public void NothingPending_NoActivity_StaysReady()
    {
        var r = new Rig();
        r.Drainer.Drain();
        Assert.True(r.Monitor.IsIdle);
        Assert.Empty(r.Seen);
        Assert.False(r.Drainer.IsBusy);
    }

    [Fact]
    public void LoadingBegins_WhenAComputeStarts_AndEndsOnlyAfterItsResultIsApplied()
    {
        var r = new Rig();
        r.Enqueue("g1");
        r.Drainer.Drain();

        Assert.Equal(ActivityKind.Loading, r.Monitor.Foreground);          // compute really in flight
        Assert.True(r.Drainer.IsBusy);

        r.Computes["g1#0"].SetResult(new GroupStorageFacts(1, 0, 1, 0));    // the off-thread work is DONE ...
        SpinWait.SpinUntil(() => !r.Ui.IsEmpty, TimeSpan.FromSeconds(30));
        Assert.Equal(ActivityKind.Loading, r.Monitor.Foreground);          // ... but the result is not applied yet: still LOADING

        while (r.Ui.TryDequeue(out var a)) a();                            // UI thread applies it and refreshes
        Assert.Equal(1, r.Refreshes);
        Assert.Null(r.Monitor.Foreground);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, null }, r.Seen);
        Assert.True(r.Queue.IsCached("g1"));
    }

    [Fact]
    public void ManyGroups_StayLoadingContinuously_AndReturnToReadyOnlyAfterTheLastOne()
    {
        var r = new Rig();
        r.Enqueue("g1", "g2", "g3");
        r.Drainer.Drain();

        r.Finish("g1");
        Assert.Equal(ActivityKind.Loading, r.Monitor.Foreground);          // g2 already started: no READY gap
        r.Finish("g2");
        Assert.Equal(ActivityKind.Loading, r.Monitor.Foreground);
        Assert.False(r.Monitor.IsIdle);
        r.Finish("g3");

        Assert.True(r.Monitor.IsIdle);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, null }, r.Seen);   // ONE busy period, never a flash of READY
        Assert.Equal(new[] { "g1", "g2", "g3" }, r.Started);
        Assert.Equal(3, r.Refreshes);
    }

    // ── Failure / cancellation / obsolescence ───────────────────────────────────

    [Fact]
    public void FailedCompute_StillEndsItsActivity_AndMarksTheGroupFailed()
    {
        var r = new Rig();
        r.Enqueue("g1");
        r.Drainer.Drain();
        r.Finish("g1", fail: true);

        Assert.True(r.Monitor.IsIdle);
        Assert.True(r.Queue.IsFailed("g1"));
        Assert.False(r.Drainer.IsBusy);
        Assert.Equal(1, r.Refreshes);                                       // the view still refreshes (warning marker)
    }

    [Fact]
    public void ACancelledCompute_EndsItsActivity()
    {
        var r = new Rig();
        r.Enqueue("g1");
        r.Drainer.Drain();
        var before = r.Ui.Count;
        r.Computes["g1#0"].SetCanceled();
        SpinWait.SpinUntil(() => r.Ui.Count > before, TimeSpan.FromSeconds(30));
        while (r.Ui.TryDequeue(out var a)) a();

        Assert.True(r.Monitor.IsIdle);
        Assert.True(r.Queue.IsFailed("g1"));
    }

    [Fact]
    public void FailureOfOneGroup_DoesNotStopTheNext_OrLeakTheActivity()
    {
        var r = new Rig();
        r.Enqueue("g1", "g2");
        r.Drainer.Drain();
        r.Finish("g1", fail: true);
        Assert.Equal(ActivityKind.Loading, r.Monitor.Foreground);          // g2 is now loading
        r.Finish("g2");
        Assert.True(r.Monitor.IsIdle);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, null }, r.Seen);
    }

    [Fact]
    public void SupersededCompute_OldFinishNeverClearsTheNewOne()
    {
        var r = new Rig();
        r.Enqueue("g1");
        r.Drainer.Drain();                                                  // old compute (generation 0) in flight

        r.Queue.Invalidate("g1");                                           // the group is invalidated mid-flight → old result obsolete
        r.Finish("g1", nth: 0);                                             // old finishes: result rejected → re-enqueued → NEW compute

        Assert.Equal(2, r.Started.Count(s => s == "g1"));
        Assert.Equal(ActivityKind.Loading, r.Monitor.Foreground);          // the newer compute is still being waited for
        Assert.False(r.Queue.IsCached("g1"));

        r.Finish("g1", nth: 1);
        Assert.True(r.Monitor.IsIdle);
        Assert.True(r.Queue.IsCached("g1"));
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, null }, r.Seen);
    }

    [Fact]
    public void RefreshThrowing_StillReleasesTheActivity()
    {
        var r = new Rig();
        r.OnRefresh = () => throw new InvalidOperationException("view refresh failed");
        r.Enqueue("g1");
        r.Drainer.Drain();

        var before = r.Ui.Count;
        r.Computes["g1#0"].SetResult(default);
        SpinWait.SpinUntil(() => r.Ui.Count > before, TimeSpan.FromSeconds(30));
        Assert.Throws<InvalidOperationException>(() => { while (r.Ui.TryDequeue(out var a)) a(); });

        Assert.True(r.Monitor.IsIdle);                                      // no leaked busy state
    }

    [Fact]
    public void ComputeThrowingSynchronously_DoesNotLeakTheActivity()
    {
        var queue = new GroupCoverageQueue();
        var monitor = new ActivityMonitor();
        queue.Enqueue("g1", false, _ => true);
        var drainer = new GroupCoverageDrainer(queue, _ => true, _ => NoLeaves,
            _ => throw new InvalidOperationException("cannot start"), a => a(), () => { }, monitor);

        Assert.Throws<InvalidOperationException>(drainer.Drain);
        Assert.True(monitor.IsIdle);
    }

    // ── Concurrency with other activities, repetition ───────────────────────────

    [Fact]
    public void Verification_DuringLoading_ShowsVerifying_ThenFallsBackToLoading_ThenReady()
    {
        var r = new Rig();
        r.Enqueue("g1");
        r.Drainer.Drain();                                                  // Systems refresh → LOADING

        var verify = r.Monitor.Begin(ActivityKind.Verifying);               // Verify joins
        Assert.Equal(ActivityKind.Verifying, r.Monitor.Foreground);
        verify.Dispose();                                                   // Verify ends while the refresh continues
        Assert.Equal(ActivityKind.Loading, r.Monitor.Foreground);

        r.Finish("g1");                                                     // final System finished
        Assert.Null(r.Monitor.Foreground);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, ActivityKind.Verifying, ActivityKind.Loading, null }, r.Seen);
    }

    [Fact]
    public void LoadingFinishesFirst_WhileAVerificationContinues_ShowsVerifyingNotReady()
    {
        var r = new Rig();
        r.Enqueue("g1");
        r.Drainer.Drain();
        var verify = r.Monitor.Begin(ActivityKind.Verifying);
        r.Finish("g1");
        Assert.Equal(ActivityKind.Verifying, r.Monitor.Foreground);
        verify.Dispose();
        Assert.True(r.Monitor.IsIdle);
    }

    [Fact]
    public void SecondDrainWhileBusy_DoesNothing_OneWorkerOneActivity()
    {
        var r = new Rig();
        r.Enqueue("g1", "g2");
        r.Drainer.Drain();
        r.Drainer.Drain();
        r.Drainer.Drain();
        Assert.Single(r.Started);
        Assert.Equal(1, r.Monitor.ActiveCount);
        r.Finish("g1"); r.Finish("g2");
        Assert.True(r.Monitor.IsIdle);
    }

    [Fact]
    public void RepeatedRefreshCycles_NeverLeakABusyState()
    {
        var r = new Rig();
        for (int cycle = 0; cycle < 25; cycle++)
        {
            r.Queue.Invalidate("g1");                                       // each cycle: the group becomes pending again
            r.Queue.Invalidate("g2");
            r.Enqueue("g1", "g2");
            r.Drainer.Drain();
            Assert.Equal(ActivityKind.Loading, r.Monitor.Foreground);
            r.Finish("g1", nth: cycle);
            r.Finish("g2", nth: cycle);
            Assert.True(r.Monitor.IsIdle, $"leaked busy state after cycle {cycle}");
            Assert.False(r.Drainer.IsBusy);
        }
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, null }.Length * 25, r.Seen.Count);
    }

    [Fact]
    public void OverlappingRefreshRequests_WhileAGroupIsInFlight_EndOnlyWhenEverythingIsDone()
    {
        var r = new Rig();
        r.Enqueue("g1");
        r.Drainer.Drain();
        r.Enqueue("g2");                                                    // a second refresh request arrives mid-flight
        r.Queue.Enqueue("g3", priority: true, _ => true);                   // and a priority one (selection)

        r.Finish("g1");
        Assert.Equal(ActivityKind.Loading, r.Monitor.Foreground);
        Assert.Equal(new[] { "g1", "g3" }, r.Started);                      // priority group jumped the queue
        r.Finish("g3");
        Assert.Equal(ActivityKind.Loading, r.Monitor.Foreground);
        r.Finish("g2");
        Assert.True(r.Monitor.IsIdle);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, null }, r.Seen);
    }

    // ── No fake delays / timeouts; scope of the integration ─────────────────────

    [Fact]
    public void Drainer_UsesNoTimersOrDelays_AndNoOtherWorkflowIsConnected()
    {
        var src = File.ReadAllText(RepoFile("Systems/GroupCoverageDrainer.cs"));
        Assert.DoesNotContain("Delay", src);
        Assert.DoesNotContain("Timer", src);
        Assert.DoesNotContain("Thread.Sleep", src);
        Assert.Equal(new[] { "ActivityKind.Loading" },
            System.Text.RegularExpressions.Regex.Matches(src, @"ActivityKind\.\w+").Select(m => m.Value).Distinct());   // only Loading

        // The Systems coverage worker keeps its single LOADING registration (the Drainer above); the later
        // integration adds other LOADING scopes elsewhere (dashboard / Group planning), never inside the drainer.
    }

    [Fact]
    public void MainWindow_DelegatesTheDrainToTheTestedWorker_WithTheSharedMonitor()
    {
        var code = File.ReadAllText(RepoFile("MainWindow.axaml.cs"));
        Assert.Contains("private void DrainGroupCoverageQueue() => GroupCoverageDrainerInstance.Drain();", code);
        Assert.Contains("Activities.ActivityMonitor.Shared);", code);
        Assert.DoesNotContain("_groupCoverageBusy", code);
    }

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }
}
