using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Arkadia.Activities;
using Arkadia.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// C64-ACTIVITY-2 / IMPORT-1: a real Single DAT import registers IMPORTING on the shared ActivityMonitor for exactly the
/// duration of the actual processing. Real catalog + leaf databases in temp directories; the monitor only observes.
/// </summary>
public sealed class SingleDatImportActivityTests : IDisposable
{
    private readonly string _dir;

    public SingleDatImportActivityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkImportAct_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CatalogService NewCatalog()
    {
        var c = new CatalogService(_dir);
        if (c.GetHardwareFamily("nds") is null)
            c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
            {
                new() { Id = "nds", Name = "NDS", Manufacturer = "N", HardwareTypeId = "console" },
            });
        return c;
    }

    private static SingleDatImportRequest Req(string variant = "main", string display = "NoIntro - Main", int games = 3)
        => new()
        {
            HardwareFamilyId = "nds", Authority = "nointro", MediaTypeId = "cartridge", Variant = variant,
            DisplayName = display, Version = "v1",
            Games = Enumerable.Range(0, games).Select(i => new DatParser.ParsedGame
            {
                Name = $"Game {i}",
                Roms = [new DatParser.ParsedRom { Name = $"g{i}.bin", Size = "10", Sha1 = new string((char)('a' + i), 40) }],
                ContentKey = "sha1:" + new string((char)('a' + i), 40),
            }).ToList(),
        };

    private static (ActivityMonitor Monitor, List<ActivityKind?> Seen) Observed()
    {
        var m = new ActivityMonitor();
        var seen = new List<ActivityKind?>();
        m.ForegroundChanged += seen.Add;
        return (m, seen);
    }

    private static List<(string Id, string Name, string Authority, string Media, string Version, int Count, string Path, string? Display)> Snapshot(CatalogService c)
        => c.LoadDatLines().OrderBy(l => l.Id, StringComparer.Ordinal)
            .Select(l => (l.Id, l.Name, l.Authority, l.MediaTypeId, l.Version, l.ReleaseCount, l.DataStorePath, l.DisplayName)).ToList();

    // ── Success ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Success_ImportingIsActiveWhileProcessing_AndReadyAfterwards()
    {
        var c = NewCatalog();
        var (m, seen) = Observed();
        ActivityKind? duringBuild = null, duringPublish = null;
        var svc = new SingleDatImportService(c, _dir, m)
        {
            OnLeafBuiltForTests     = () => duringBuild   = m.Foreground,    // database prepared
            OnLeafPublishedForTests = () => duringPublish = m.Foreground,    // published, catalog update still to come
        };

        var r = svc.Execute(Req(), null, CancellationToken.None);

        Assert.True(r.Success, r.Message);
        Assert.Equal(ActivityKind.Importing, duringBuild);
        Assert.Equal(ActivityKind.Importing, duringPublish);
        Assert.True(m.IsIdle);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Importing, null }, seen);       // one busy period, no flicker
        Assert.Single(c.LoadDatLines());
    }

    [Fact]
    public void ImportingStaysActive_UntilTheCatalogRowIsCommitted_AndTempCleanupIsDone()
    {
        var c = NewCatalog();
        var (m, _) = Observed();
        bool rowVisibleWhileImporting = true;
        var svc = new SingleDatImportService(c, _dir, m)
        {
            OnLeafPublishedForTests = () => rowVisibleWhileImporting = c.LoadDatLines().Count > 0,   // catalog not written yet
        };
        var r = svc.Execute(Req(), null, CancellationToken.None);
        Assert.True(r.Success);
        Assert.False(rowVisibleWhileImporting);                                          // still importing: row appears later

        // after Execute returns, the scope is gone AND the work is complete: row committed, no temp files left
        Assert.True(m.IsIdle);
        Assert.Single(c.LoadDatLines());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "systems"), "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ProgressCallbacks_ArriveWhileImportingIsStillActive()
    {
        var c = NewCatalog();
        var (m, _) = Observed();
        var seenDuringProgress = new List<ActivityKind?>();
        var progress = new SyncProgress<LeafDatBuildProgress>(_ => seenDuringProgress.Add(m.Foreground));

        new SingleDatImportService(c, _dir, m).Execute(Req(games: 60), progress, CancellationToken.None);

        Assert.NotEmpty(seenDuringProgress);
        Assert.All(seenDuringProgress, k => Assert.Equal(ActivityKind.Importing, k));
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }

    // ── Failure / validation / cancellation ─────────────────────────────────────

    [Fact]
    public void ValidationFailure_NeverLeavesTheConsoleBusy_AndWritesNothing()
    {
        var c = NewCatalog();
        var (m, seen) = Observed();
        var svc = new SingleDatImportService(c, _dir, m);

        foreach (var bad in new[] { Req(variant: "Bad Value"), Req(display: "  "), Req(games: 0) })
        {
            var r = svc.Execute(bad, null, CancellationToken.None);
            Assert.False(r.Success);
            Assert.True(m.IsIdle);
        }
        Assert.Empty(c.LoadDatLines());
        Assert.Equal(0, seen.Count % 2);                                                 // every start has its end
        Assert.Null(seen.LastOrDefault());
    }

    [Fact]
    public void CollisionFailure_EndsTheActivity_AndLeavesTheExistingDatUntouched()
    {
        var c = NewCatalog();
        var plain = new SingleDatImportService(c, _dir);
        Assert.True(plain.Execute(Req(), null, CancellationToken.None).Success);
        var before = Snapshot(c);

        var (m, seen) = Observed();
        var r = new SingleDatImportService(c, _dir, m).Execute(Req(), null, CancellationToken.None);   // same id → collision

        Assert.False(r.Success);
        Assert.True(m.IsIdle);
        Assert.Equal(before, Snapshot(c));
        Assert.Null(seen.Last());
    }

    [Fact]
    public void FailureInTheMiddleOfProcessing_EndsTheActivity_AfterCleanup()
    {
        var c = NewCatalog();
        var (m, seen) = Observed();
        var svc = new SingleDatImportService(c, _dir, m)
        {
            // another actor claims the id (case variant) while the temp DB is built → second validation fails
            OnLeafBuiltForTests = () => c.SaveDatLines(new List<DatLineRecord>
            {
                new() { Id = "NDS-NOINTRO-CARTRIDGE-MAIN", HardwareFamilyId = "nds", Name = "x", Authority = "nointro",
                        MediaTypeId = "cartridge", DataStorePath = "systems/nds/other.db", ImportedAtUtc = DateTime.UtcNow },
            }),
        };

        var r = svc.Execute(Req(), null, CancellationToken.None);

        Assert.Equal(SingleDatImportError.IdCollision, r.Error);
        Assert.True(m.IsIdle);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Importing, null }, seen);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "systems"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Cancellation_BeforeProcessing_EndsTheActivity()
    {
        var c = NewCatalog();
        var (m, seen) = Observed();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = new SingleDatImportService(c, _dir, m).Execute(Req(), null, cts.Token);

        Assert.Equal(SingleDatImportError.Cancelled, r.Error);
        Assert.True(m.IsIdle);
        Assert.Null(seen.Last());
        Assert.Empty(c.LoadDatLines());
    }

    [Fact]
    public void Cancellation_MidProcessing_EndsTheActivityOnlyAfterCleanup_AndLeavesNoFiles()
    {
        var c = NewCatalog();
        var (m, seen) = Observed();
        using var cts = new CancellationTokenSource();
        ActivityKind? atCancel = null;
        var svc = new SingleDatImportService(c, _dir, m)
        {
            OnLeafBuiltForTests = () => { atCancel = m.Foreground; cts.Cancel(); },    // user cancels after the temp DB exists
        };

        var r = svc.Execute(Req(), null, cts.Token);

        Assert.Equal(SingleDatImportError.Cancelled, r.Error);
        Assert.Equal(ActivityKind.Importing, atCancel);
        Assert.True(m.IsIdle);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Importing, null }, seen);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "systems"), "*", SearchOption.AllDirectories));   // temp removed first
        Assert.Empty(c.LoadDatLines());
    }

    [Fact]
    public void UnexpectedException_StillReleasesTheActivity()
    {
        var c = NewCatalog();
        var (m, seen) = Observed();
        var svc = new SingleDatImportService(c, _dir, m)
        {
            OnLeafBuiltForTests = () => throw new InvalidOperationException("boom"),
        };

        Assert.Throws<InvalidOperationException>(() => svc.Execute(Req(), null, CancellationToken.None));

        Assert.True(m.IsIdle);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Importing, null }, seen);
    }

    // ── Repetition, overlap, priorities ─────────────────────────────────────────

    [Fact]
    public void RepeatedImports_EachGetsItsOwnBusyPeriod_AndNothingLeaks()
    {
        var c = NewCatalog();
        var (m, seen) = Observed();
        var svc = new SingleDatImportService(c, _dir, m);

        for (int i = 0; i < 12; i++)
        {
            Assert.True(svc.Execute(Req(variant: $"v{i}", display: $"Name {i}"), null, CancellationToken.None).Success);
            Assert.True(m.IsIdle, $"leaked scope after import {i}");
        }
        Assert.Equal(12, c.LoadDatLines().Count);
        Assert.Equal(24, seen.Count);
        for (int i = 0; i < seen.Count; i++) Assert.Equal(i % 2 == 0 ? ActivityKind.Importing : null, seen[i]);
    }

    [Fact]
    public void ImportDuringLoading_ShowsImporting_ThenFallsBackToLoading_ThenReady()
    {
        var c = NewCatalog();
        var (m, seen) = Observed();
        var loading = m.Begin(ActivityKind.Loading, "Systems refresh");
        ActivityKind? during = null;
        var svc = new SingleDatImportService(c, _dir, m) { OnLeafBuiltForTests = () => during = m.Foreground };

        Assert.True(svc.Execute(Req(), null, CancellationToken.None).Success);

        Assert.Equal(ActivityKind.Importing, during);                       // higher priority wins the foreground
        Assert.Equal(ActivityKind.Loading, m.Foreground);                   // import ended; the refresh continues → NOT ready
        Assert.False(m.IsIdle);

        loading.Dispose();
        Assert.True(m.IsIdle);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Loading, ActivityKind.Importing, ActivityKind.Loading, null }, seen);
    }

    [Fact]
    public void ImportEndingFirst_NeverCancelsAnotherActivity_AndLoadingEndingFirstKeepsImporting()
    {
        var c = NewCatalog();
        var (m, _) = Observed();
        IActivityScope? loading = m.Begin(ActivityKind.Loading);
        ActivityKind? afterLoadingEnded = null;
        var svc = new SingleDatImportService(c, _dir, m)
        {
            OnLeafBuiltForTests = () => { loading!.Dispose(); afterLoadingEnded = m.Foreground; },   // refresh finishes mid-import
        };
        svc.Execute(Req(), null, CancellationToken.None);
        Assert.Equal(ActivityKind.Importing, afterLoadingEnded);            // no premature READY while the import runs
        Assert.True(m.IsIdle);
    }

    [Fact]
    public void ExistingPriorities_AreUntouched_ImportingOutranksVerifyingAndLoading_ButNotIngestingOrMoving()
    {
        Assert.True(ActivityCatalog.Priority(ActivityKind.Importing) > ActivityCatalog.Priority(ActivityKind.Verifying));
        Assert.True(ActivityCatalog.Priority(ActivityKind.Importing) > ActivityCatalog.Priority(ActivityKind.Loading));
        Assert.True(ActivityCatalog.Priority(ActivityKind.Importing) < ActivityCatalog.Priority(ActivityKind.Ingesting));
        Assert.True(ActivityCatalog.Priority(ActivityKind.Importing) < ActivityCatalog.Priority(ActivityKind.Moving));
        Assert.Equal(ActivityPattern.HexStream, ActivityCatalog.PatternFor(ActivityKind.Importing));
        Assert.Equal("IMPORTING...", ActivityCatalog.Label(ActivityKind.Importing));
    }

    // ── A modal progress dialog does not delay or hide the activity ─────────────

    [Fact]
    public void ModalProgressDialog_PumpsTheDispatcher_SoTheConsoleShowsImportingDuringTheImport()
    {
        // ShowDialog runs a nested dispatcher loop: queued notifications ARE processed while the dialog is open.
        var queue = new Queue<Action>();
        var m = new ActivityMonitor(queue.Enqueue);
        var seen = new List<ActivityKind?>();
        m.ForegroundChanged += seen.Add;
        void PumpDispatcher() { while (queue.Count > 0) queue.Dequeue()(); }

        var c = NewCatalog();
        ActivityKind? consoleMidImport = null;
        var svc = new SingleDatImportService(c, _dir, m)
        {
            OnLeafBuiltForTests = () => { PumpDispatcher(); consoleMidImport = seen.LastOrDefault(); },
        };

        svc.Execute(Req(), null, CancellationToken.None);
        PumpDispatcher();

        Assert.Equal(ActivityKind.Importing, consoleMidImport);             // the console showed IMPORTING while the work was running
        Assert.Equal(new ActivityKind?[] { ActivityKind.Importing, null }, seen);
    }

    [Fact]
    public void AnImportFinishedWhileTheUiWasCompletelyBlocked_LeavesNoStaleBusyStateBehind()
    {
        var queue = new Queue<Action>();
        var m = new ActivityMonitor(queue.Enqueue);                         // dispatcher never pumped during the work
        var seen = new List<ActivityKind?>();
        m.ForegroundChanged += seen.Add;
        var c = NewCatalog();

        new SingleDatImportService(c, _dir, m).Execute(Req(), null, CancellationToken.None);
        Assert.True(m.IsIdle);                                              // the truth is exact at all times

        while (queue.Count > 0) queue.Dequeue()();
        Assert.Empty(seen);                                                 // coalesced: nothing to show, and never a stale IMPORTING
    }

    [Fact]
    public async System.Threading.Tasks.Task ImportOnAWorkerThread_NotifiesViaThePostDelegate_NotOnTheWorker()
    {
        var c = NewCatalog();
        var uiThreadId = Environment.CurrentManagedThreadId;
        var delivered = new List<(ActivityKind? Kind, int Thread)>();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var m = new ActivityMonitor(queue.Enqueue);
        m.ForegroundChanged += k => delivered.Add((k, Environment.CurrentManagedThreadId));

        await System.Threading.Tasks.Task.Run(() => new SingleDatImportService(c, _dir, m).Execute(Req(), null, CancellationToken.None));
        while (queue.TryDequeue(out var a)) a();                            // "UI thread" drains

        Assert.All(delivered, d => Assert.Equal(uiThreadId, d.Thread));     // listeners only ever run where the queue is pumped
        Assert.Null(delivered.LastOrDefault().Kind);
    }

    // ── The monitor changes nothing that is persisted ───────────────────────────

    [Fact]
    public void ObservedAndUnobservedImports_ProduceTheSamePersistedCatalog_AndLeafDatabase()
    {
        string RunIn(string sub, ActivityMonitor? monitor)
        {
            var dir = Path.Combine(_dir, sub);
            Directory.CreateDirectory(dir);
            var cat = new CatalogService(dir);
            cat.SaveHardwareFamilies(new List<HardwareFamilyRecord> { new() { Id = "nds", Name = "NDS", Manufacturer = "N", HardwareTypeId = "console" } });
            var r = new SingleDatImportService(cat, dir, monitor).Execute(Req(games: 25), null, CancellationToken.None);
            Assert.True(r.Success, r.Message);

            var line = Assert.Single(cat.LoadDatLines());
            var store = new DatLineStore(Path.Combine(dir, line.DataStorePath));
            var releases = store.LoadReleases().Select(x => (x.Name, x.Status, x.ReleaseContentKey)).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
            var files = store.LoadAllReleaseFiles().Values.SelectMany(v => v).Select(f => (f.RomName, f.Sha1)).OrderBy(f => f.RomName, StringComparer.Ordinal).ToList();
            return string.Join("|", line.Id, line.Name, line.Authority, line.MediaTypeId, line.Version, line.ReleaseCount, line.DataStorePath, line.DisplayName,
                string.Join(",", releases), string.Join(",", files));
        }

        Assert.Equal(RunIn("plain", null), RunIn("observed", new ActivityMonitor()));
    }

    [Fact]
    public void WithoutAMonitor_TheServiceBehavesExactlyAsBefore()
    {
        var c = NewCatalog();
        var r = new SingleDatImportService(c, _dir).Execute(Req(), null, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Single(c.LoadDatLines());
    }

    // ── Integration contract in the code (the window itself cannot run headless) ─

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    [Fact]
    public void ImportUsesTheOneSharedMonitor_AndNoWindowOwnsAnotherInstance()
    {
        var main = File.ReadAllText(RepoFile("MainWindow.axaml.cs"));
        Assert.Contains("new SingleDatImportService(_catalog, _dataDir, Activities.ActivityMonitor.Shared)", main);

        // no production code constructs a monitor explicitly: the only instance is the Shared singleton
        var constructions = Directory.EnumerateFiles(RepoFile("."), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}Arkadia.Tests{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"new ActivityMonitor\(").Select(_ => f)).ToList();
        Assert.Empty(constructions);                                                         // nobody news one up…
        Assert.Contains("ActivityMonitor Shared { get; } = new(", File.ReadAllText(RepoFile("Activities/ActivityMonitor.cs")));   // …except the singleton
    }

    [Fact]
    public void TheConfigurationDialogAndTheWindowHandlerNeverRegisterTheActivity()
    {
        // Registration lives in SingleDatImportService.Execute (i.e. only when processing starts), not in the dialog,
        // not in OnImportDat before the request is submitted.
        Assert.DoesNotContain("Activit", File.ReadAllText(RepoFile("Ingestion/ImportDatDialog.axaml.cs")));
        Assert.DoesNotContain("Activit", File.ReadAllText(RepoFile("Ingestion/DatOperationProgressDialog.axaml.cs")));

        var main = File.ReadAllText(RepoFile("MainWindow.axaml.cs"));
        var start = main.IndexOf("private async void OnImportDat", StringComparison.Ordinal);
        var end = main.IndexOf("private async void OnPreviewGroupDat", StringComparison.Ordinal);
        var handler = main.Substring(start, end - start);
        Assert.DoesNotContain(".Begin(", handler);
        Assert.DoesNotContain("ActivityKind", handler);

        var svc = File.ReadAllText(RepoFile("DataLayer/SingleDatImportService.cs"));
        Assert.Single(Regex.Matches(svc, @"_activities\?\.Begin\("));
        Assert.Contains("using var activity = _activities?.Begin(", svc);          // disposed by `using`: exactly once, on every exit
    }

    [Fact]
    public void ImportFlow_UsesNoDelaysOrFakeFlags()
    {
        var svc = File.ReadAllText(RepoFile("DataLayer/SingleDatImportService.cs"));
        Assert.DoesNotContain("Delay", svc);
        Assert.DoesNotContain("Thread.Sleep", svc);
        Assert.DoesNotContain("IsBusy", svc);
        Assert.DoesNotContain("_importing", svc);
    }

    [Fact]
    public void SingleDatImport_IsStillConnectedAsImporting_InTheService_NotInTheWindow()
    {
        // GLOBAL-ACTIVITY-INTEGRATION-1 connected the other workflows; the Single import keeps its single registration,
        // inside the service (never duplicated by the caller).
        var svc = File.ReadAllText(RepoFile("DataLayer/SingleDatImportService.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(svc, @"_activities\?\.Begin\("));
        Assert.Contains("ActivityKind.Importing", svc);
        var main = File.ReadAllText(RepoFile("MainWindow.axaml.cs"));
        Assert.DoesNotContain("\"import DAT\"", main);
    }
}
