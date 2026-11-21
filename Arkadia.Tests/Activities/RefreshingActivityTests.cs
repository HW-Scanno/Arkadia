using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Arkadia.Activities;
using Arkadia.Controls;
using Xunit;

namespace Arkadia.Tests.Activities;

/// <summary>
/// C64-REFRESHING-1 — the static <c>&lt;REFRESHING&gt;</c> state shown while the existing synchronous post-ingestion UI
/// refresh runs. The refresh is unchanged and stays on the UI thread; it is only started one dispatcher turn later so
/// the status is committed first. The pixel rendering itself was verified visually in an isolated instance.
/// </summary>
public sealed class RefreshingActivityTests
{
    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    private static string Read(string rel) => File.ReadAllText(RepoFile(rel)).Replace("\r\n", "\n");

    // ── Catalog, console rules ───────────────────────────────────────────────────

    [Fact]
    public void Label_IsExactlyAngleBracketedRefreshing()
    {
        Assert.Equal("<REFRESHING>", ActivityCatalog.Label(ActivityKind.Refreshing));
        Assert.Equal("<REFRESHING>", C64ConsoleVisuals.TextFor(ActivityKind.Refreshing));
        // every other label keeps its existing "NAME..." form
        foreach (var k in Enum.GetValues<ActivityKind>().Where(k => k != ActivityKind.Refreshing))
            Assert.Equal(k.ToString().ToUpperInvariant() + "...", ActivityCatalog.Label(k));
    }

    [Fact]
    public void Refreshing_UsesTheStaticPattern_AndNoOtherKindDoes()
    {
        Assert.Equal(ActivityPattern.Static, ActivityCatalog.PatternFor(ActivityKind.Refreshing));
        Assert.Equal(new[] { ActivityKind.Refreshing }, Enum.GetValues<ActivityKind>().Where(k => ActivityCatalog.PatternFor(k) == ActivityPattern.Static));
    }

    [Fact]
    public void Refreshing_NeverBlinks_NotEvenWithMotionEnabled()
    {
        Assert.True(Enumerable.Range(0, 3000).All(ms => C64ConsoleVisuals.TextVisible(ActivityKind.Refreshing, ms, reducedMotion: false)));
        Assert.True(Enumerable.Range(0, 3000).All(ms => C64ConsoleVisuals.TextVisible(ActivityKind.Refreshing, ms, reducedMotion: true)));
    }

    [Fact]
    public void Label_FitsTheScreen_OnTheSameGridRuleAsTheOtherKinds()
    {
        double screen = C64ConsoleVisuals.DefaultWidth - 2 * C64ConsoleVisuals.FrameThickness;
        var size = C64ConsoleVisuals.FontSizeFor("<REFRESHING>", screen);
        Assert.Equal(C64ConsoleVisuals.SmallFontSize, size);                       // 12 chars: 1x grid, like INGESTING...
        Assert.True("<REFRESHING>".Length * size <= screen);
    }

    private sealed class FakeSource : IActivityTickSource
    {
        public int StartCalls;
        public void Start(TimeSpan interval, Action tick) => StartCalls++;
        public void Stop() { }
    }

    [Fact]
    public void Refreshing_NeverSubscribesToTheClock_SoNoTimerExistsForIt()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        var c = new C64StatusConsole { Clock = clock };
        c.SetAttached(true);
        c.Kind = ActivityKind.Refreshing;
        Assert.False(c.HasClockSubscription);
        Assert.Equal(0, clock.SubscriberCount);
        Assert.Equal(0, src.StartCalls);

        c.Kind = ActivityKind.Verifying;                                           // animated kinds still do
        Assert.True(c.HasClockSubscription);
        c.Kind = ActivityKind.Refreshing;                                          // switching to the static kind releases it
        Assert.False(c.HasClockSubscription);
        Assert.Equal(0, clock.SubscriberCount);
    }

    [Fact]
    public void Console_DrawsRefreshingWithTheExistingBlueStyling_NoStripsNoRedBlack()
    {
        var src = Read("Controls/C64StatusConsole.cs");
        var render = src.Substring(src.IndexOf("public override void Render(", StringComparison.Ordinal));
        render = render.Substring(0, render.IndexOf("private static void DrawRaster", StringComparison.Ordinal));
        Assert.Contains("p != ActivityPattern.Static", render);                    // no pattern strips for the static state
        Assert.Contains("lava ? BlackBrush : BlueBrush", render);                  // black/red remains PURGING-only
        Assert.DoesNotContain("Refreshing", render);                               // no special-casing: same styling path as READY
    }

    // ── Priority and overlap ─────────────────────────────────────────────────────

    [Fact]
    public void Priority_SitsAboveLoading_AndBelowEveryRealOperation()
    {
        var p = ActivityCatalog.Priority(ActivityKind.Refreshing);
        Assert.True(p > ActivityCatalog.Priority(ActivityKind.Loading));
        foreach (var k in Enum.GetValues<ActivityKind>().Where(k => k is not (ActivityKind.Loading or ActivityKind.Refreshing)))
            Assert.True(p < ActivityCatalog.Priority(k), k.ToString());
        Assert.Equal(ActivityKind.Loading, ActivityCatalog.PriorityLowToHigh[0]);   // LOADING stays the lowest
    }

    [Fact]
    public void WithBackgroundLoading_RefreshingShowsOverIt_ThenLoadingReturns_NeverReady()
    {
        var rec = new ActivityRecorder();
        using var loading = rec.Monitor.Begin(ActivityKind.Loading);               // background Group coverage
        using (rec.Monitor.Begin(ActivityKind.Refreshing)) { }
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, ActivityKind.Refreshing, ActivityKind.Loading }, rec.Seen);
    }

    [Fact]
    public void ALongerOperationStaysInFront_OfRefreshing()
    {
        var rec = new ActivityRecorder();
        using var ingest = rec.Monitor.Begin(ActivityKind.Ingesting);
        using (rec.Monitor.Begin(ActivityKind.Refreshing)) Assert.Equal(ActivityKind.Ingesting, rec.Now);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Ingesting }, rec.Seen);
    }

    // ── DeferredRefresh ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Deferred_ScopeBeginsBeforeTheRender_RefreshRunsInsideIt_AndEndsAfter()
    {
        var rec = new ActivityRecorder();
        var order = new List<string>();
        await DeferredRefresh.RunAsync(rec.Monitor,
            () => { order.Add("render-opportunity:" + rec.Now); return Task.CompletedTask; },
            () => order.Add("refresh:" + rec.Now));
        order.Add("after:" + (rec.Now?.ToString() ?? "READY"));

        Assert.Equal(new[] { "render-opportunity:Refreshing", "refresh:Refreshing", "after:READY" }, order);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Refreshing, null }, rec.Seen);
    }

    [Fact]
    public async Task Deferred_ExceptionInTheRefresh_PropagatesAndReleasesTheScope()
    {
        var rec = new ActivityRecorder();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeferredRefresh.RunAsync(rec.Monitor,
            () => Task.CompletedTask, () => throw new InvalidOperationException("refresh failed")));
        Assert.True(rec.Idle);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Refreshing, null }, rec.Seen);
    }

    [Fact]
    public async Task Deferred_ExceptionWhileWaitingForTheRender_ReleasesTheScope_AndNeverRunsTheRefresh()
    {
        var rec = new ActivityRecorder();
        bool ran = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeferredRefresh.RunAsync(rec.Monitor,
            () => Task.FromException(new InvalidOperationException("dispatcher gone")), () => ran = true));
        Assert.False(ran);
        Assert.True(rec.Idle);
    }

    /// <summary>
    /// A tiny model of the real dispatcher: Normal jobs run first; a frame is produced only on a "vsync" tick, AFTER the
    /// Normal queue is empty, and renders whatever the console currently shows. (Measured in the real app: a Background
    /// job runs BEFORE the next frame is produced, so waiting on a dispatcher yield painted the text only after the refresh.)
    /// </summary>
    private sealed class FramePump
    {
        private readonly Queue<Action> _normal = new();
        private readonly List<Action> _frameCallbacks = new();
        public ActivityKind? ConsoleShows;          // what the console currently holds (set by the notification)
        public ActivityKind? LastRendered;          // what the last produced frame painted

        public void PostNormal(Action a) => _normal.Enqueue(a);

        public Task NextFrame()
        {
            var tcs = new TaskCompletionSource();
            _frameCallbacks.Add(() => tcs.SetResult());
            return tcs.Task;
        }

        public void Run(int maxFrames = 10)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                while (_normal.Count > 0) _normal.Dequeue()();      // all Normal jobs first
                var callbacks = _frameCallbacks.ToList(); _frameCallbacks.Clear();
                foreach (var cb in callbacks) cb();                  // frame callbacks run at the START of a frame…
                LastRendered = ConsoleShows;                         // …then the frame renders the console
                if (_normal.Count == 0 && _frameCallbacks.Count == 0) break;
            }
        }
    }

    [Fact]
    public void Deferred_TheRefreshStartsOnlyAfterAFrameHasPaintedTheStatus_WhenWaitingForTwoFrames()
    {
        var pump = new FramePump();
        var monitor = new ActivityMonitor(pump.PostNormal);
        monitor.ForegroundChanged += k => pump.ConsoleShows = k;
        ActivityKind? paintedWhenRefreshStarted = ActivityKind.Loading;   // sentinel

        var task = DeferredRefresh.RunAsync(monitor,
            async () => { await pump.NextFrame(); await pump.NextFrame(); },
            () => paintedWhenRefreshStarted = pump.LastRendered);
        pump.Run();

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(ActivityKind.Refreshing, paintedWhenRefreshStarted);          // the text was already on screen
        Assert.Null(pump.ConsoleShows);                                            // and READY returned afterwards
    }

    [Fact]
    public void Deferred_AMereDispatcherYieldWouldStartTheRefreshBeforeAnyFrame_WhichIsWhyFramesAreAwaited()
    {
        var pump = new FramePump();
        var monitor = new ActivityMonitor(pump.PostNormal);
        monitor.ForegroundChanged += k => pump.ConsoleShows = k;
        ActivityKind? paintedWhenRefreshStarted = ActivityKind.Loading;

        // Task.CompletedTask / a yield: the refresh starts in the same turn, before the frame ever renders.
        var task = DeferredRefresh.RunAsync(monitor, () => Task.CompletedTask, () => paintedWhenRefreshStarted = pump.LastRendered);
        pump.Run();

        Assert.True(task.IsCompletedSuccessfully);
        Assert.NotEqual(ActivityKind.Refreshing, paintedWhenRefreshStarted);       // not painted: the bug the PO observed
    }

    [Fact]
    public void Deferred_UsesNoTimersDelaysOrThreads_AndTheRefreshIsNotMovedOffTheCallingThread()
    {
        var src = Read("Activities/DeferredRefresh.cs");
        foreach (var banned in new[] { "Task.Run", "Task.Delay", "Task.Yield", "Thread.Sleep", "Timer", "ConfigureAwait(false)", "DoEvents", "RunJobs" })
            Assert.DoesNotContain(banned, src);
        Assert.Matches(@"await letStatusRender\(\);\s*refresh\(\);", src);          // refresh runs on the continuation of the awaiting (UI) context
    }

    // ── Wiring: the post-ingestion sequences ─────────────────────────────────────

    private static string Main => Read("MainWindow.axaml.cs");

    [Fact]
    public void SingleIngestion_TheOriginalSequenceIsWrappedUnchanged_AndOnlyWhenARefreshWillRun()
    {
        var m = Main;
        var start = m.IndexOf("void PostIngestSequence()", StringComparison.Ordinal);
        var end   = m.IndexOf("private System.Threading.Tasks.Task RunRefreshingAsync(", start, StringComparison.Ordinal);
        var seq = m.Substring(start, end - start);

        // the statements, in their original order
        var order = new[] { "WriteIngestionLog(logsDir, datLineId, ingestResult);", "RebuildLibraryDatasets();", "ResolveFlagImages();",
                            "RefreshSystemsKeepSelection(platformId);", "RefreshPending();", "RefreshStaging();" };
        int last = -1;
        foreach (var s in order) { var i = seq.IndexOf(s, StringComparison.Ordinal); Assert.True(i > last, s); last = i; }

        // REFRESHING only when a view refresh will run; otherwise the sequence (just the log) runs plainly as before
        Assert.Contains("await RunRefreshingAsync(PostIngestSequence, \"post-ingestion refresh\");", seq);
        Assert.Contains("else\n            PostIngestSequence();", seq);
        Assert.Contains("(ingestResult.Error is null && ingestResult.ReleasesPresent > 0) || Ingestion.IngestionSummary.ShouldRefreshAfterIngest(ingestResult)", seq);

        // it follows the dialog and the awaited worker — INGESTING (scope inside the worker) is already over
        var before = m.Substring(m.LastIndexOf("await progressDialog.ShowDialog<bool>(this);", start, StringComparison.Ordinal), start - m.LastIndexOf("await progressDialog.ShowDialog<bool>(this);", start, StringComparison.Ordinal));
        Assert.Contains("await workTask;", before);
        Assert.DoesNotContain("Begin(", before);
    }

    [Fact]
    public void GroupIngestion_TheOriginalSequenceIsWrappedUnchanged_InTheSameOrder()
    {
        var g = Main;
        var start = g.IndexOf("await RunRefreshingAsync(() =>", StringComparison.Ordinal);
        var end   = g.IndexOf("\"post-Group-ingestion refresh\");", start, StringComparison.Ordinal);
        var seq = g.Substring(start, end - start);
        var order = new[] { "InvalidateGroupCoverage(groupId);", "RebuildLibraryDatasets();", "ResolveFlagImages();",
                            "RefreshSystemsKeepSelection(platformId);", "RefreshPending();", "RefreshStaging();" };
        int last = -1;
        foreach (var s in order) { var i = seq.IndexOf(s, StringComparison.Ordinal); Assert.True(i > last, s); last = i; }
        // guarded exactly as before: only a non-aborted run with a summary
        Assert.Contains("if (summary is not null && !summary.Aborted)", g.Substring(start - 400, 400));
        // the Group worker keeps its single INGESTING parent scope, released inside the worker before the dialog closes
        Assert.Single(Regex.Matches(g.Substring(g.IndexOf("ExecuteGroupIngestionAsync(\n", StringComparison.Ordinal), start - g.IndexOf("ExecuteGroupIngestionAsync(\n", StringComparison.Ordinal)), @"ActivityKind\.Ingesting"));
    }

    [Fact]
    public void RefreshingHelper_WaitsForTwoRealFrames_AndAddsNoThreadSwitch()
    {
        var m = Main;
        var h = m.Substring(m.IndexOf("private System.Threading.Tasks.Task RunRefreshingAsync(", StringComparison.Ordinal), 900);
        Assert.Contains("Activities.DeferredRefresh.RunAsync(Activities.ActivityMonitor.Shared, LetStatusRenderAsync, refresh, detail)", h);
        var frames = m.Substring(m.IndexOf("private async System.Threading.Tasks.Task LetStatusRenderAsync()", StringComparison.Ordinal), 900);
        Assert.Equal(2, Regex.Matches(frames.Substring(0, frames.IndexOf("private async System.Threading.Tasks.Task NextFrameAsync()", StringComparison.Ordinal)), @"await NextFrameAsync\(\);").Count);
        Assert.Contains("RequestAnimationFrame(_ => frame.TrySetResult());", frames);
        Assert.DoesNotContain("DispatcherPriority.Background", frames);            // a Background yield measurably does NOT paint first
        Assert.DoesNotContain("Task.Run", h);
        // Ingestion itself is untouched: the worker lambdas still own the INGESTING scopes
        Assert.Contains("using var activity = Activities.ActivityMonitor.Shared.Begin(Activities.ActivityKind.Ingesting, \"ingest\");", m);
        Assert.Contains("using var groupActivity = Activities.ActivityMonitor.Shared.Begin(Activities.ActivityKind.Ingesting, \"Group ingest\");", m);
        // REFRESHING is registered ONLY by the helper (no other call site invents it)
        var other = Directory.EnumerateFiles(RepoFile("."), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && !f.Contains(@"\Arkadia.Tests\"))
            .Where(f => File.ReadAllText(f).Contains("ActivityKind.Refreshing")).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "ActivityKind.cs", "DeferredRefresh.cs" }, other);
    }

    [Fact]
    public void ExistingActivityIntegrations_AreUntouched_MoveVolumeAndLoadingCoverageIncluded()
    {
        var m = Main;
        Assert.Contains("var moveActivity = Activities.ActivityMonitor.Shared.Begin(Activities.ActivityKind.Moving", m);
        Assert.Contains("moveActivity.Dispose();", m);
        Assert.Contains("private void DrainGroupCoverageQueue() => GroupCoverageDrainerInstance.Drain();", m);
        var resolver = Read("DataLayer/VolumePathResolver.cs");
        Assert.True(resolver.IndexOf("// 1. Workspace path", StringComparison.Ordinal) < resolver.IndexOf("// 2. Mounted disk path", StringComparison.Ordinal));
    }
}
