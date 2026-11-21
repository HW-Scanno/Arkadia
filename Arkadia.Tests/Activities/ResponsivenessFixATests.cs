using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Activities;
using Arkadia.Data;
using Arkadia.Purge;
using Xunit;

namespace Arkadia.Tests.Activities;

/// <summary>
/// UI-RESPONSIVENESS-1 / FIX-A — Purge Release, ScreenScraper cache registration, Verify All and the Move Volume source
/// cleanup now run on the thread pool. MainWindow / the dialogs cannot be built headless, so the behaviour is proven on the
/// REAL services under a single-threaded "UI pump" (a SynchronizationContext like the dispatcher): the service runs off the
/// pump thread, the pump keeps processing work WHILE the service is blocked inside its activity scope, the continuation
/// returns on the pump, exceptions propagate unchanged and the scope ends. The wiring that cannot run headless (flags,
/// button states, ordering around the Move commit) is pinned on the source.
/// </summary>
public sealed class ResponsivenessFixATests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ArkResp_" + Guid.NewGuid().ToString("N")[..10]);

    public ResponsivenessFixATests() => Directory.CreateDirectory(_tmp);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    // ── A dispatcher stand-in ───────────────────────────────────────────────────────────────────────────────────

    private sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Cb, object? State)> _queue = new();
        private readonly Thread _thread;
        public int ThreadId { get; private set; }

        public Pump()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                SetSynchronizationContext(this);
                ready.Set();
                foreach (var (cb, st) in _queue.GetConsumingEnumerable()) cb(st);
            }) { IsBackground = true, Name = "test-ui-pump" };
            _thread.Start();
            ready.Wait();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public void Dispose() => _queue.CompleteAdding();

        public Task<T> Run<T>(Func<Task<T>> body)
        {
            var tcs = new TaskCompletionSource<T>();
            Post(async _ =>
            {
                try { tcs.SetResult(await body()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return tcs.Task;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> exactly like the handlers do (<c>await Task.Run(...)</c> on the pump), BLOCKS the worker
    /// inside its scope and proves the pump still processes posted work, then lets it finish.
    /// </summary>
    private async Task<(T Result, int WorkerThread, int ContinuationThread, int PumpThread, bool PumpStayedResponsive, ActivityRecorder Rec)>
        RunLikeAHandler<T>(Func<ActivityMonitor, T> work)
    {
        using var pump = new Pump();
        var rec = new ActivityRecorder();
        var entered = new TaskCompletionSource();
        var release = new ManualResetEventSlim();
        int workerThread = -1;
        rec.Monitor.ForegroundChanged += k =>
        {
            if (k is null || workerThread != -1) return;
            workerThread = Environment.CurrentManagedThreadId;       // fires synchronously on the thread that begins the scope
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30));                  // hold the worker INSIDE the operation
        };

        bool responsive = false;
        var outcome = await pump.Run(async () =>
        {
            var task = Task.Run(() => work(rec.Monitor));
            await entered.Task;                                      // the work is now in flight (blocked in its scope)

            var marker = new TaskCompletionSource();
            SynchronizationContext.Current!.Post(_ => marker.SetResult(), null);
            await marker.Task;                                       // pump alive while the operation is running
            responsive = !task.IsCompleted;

            release.Set();
            var result = await task;
            return (result, Environment.CurrentManagedThreadId);     // continuation back on the pump
        });
        return (outcome.result, workerThread, outcome.Item2, pump.ThreadId, responsive, rec);
    }

    // ── 4. ScreenScraper cache registration (IndexPackage) ──────────────────────────────────────────────────────

    private const string Manifest = """{"version":1,"provider":"screenscraper","cacheProviderId":"screenscraper-cache","systemId":"75","systemName":"Capcom","builtAtUtc":"2026-05-01T00:00:00Z","gameCount":2}""";
    private const string Payload = """{"response":{"jeu":{"id":"1001","noms":[{"region":"wor","text":"1942"}],"developpeur":{"text":"Capcom"},"editeur":{"text":"Capcom"},"dates":[{"region":"wor","text":"1984"}],"langues":[{"shortname":"en"}],"synopsis":[{"langue":"en","text":"Shoot em up"}]}}}""";

    private string MakeZip(string name)
    {
        var path = Path.Combine(_tmp, name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string entry, string text) { using var w = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false)); w.Write(text); }
        Add("manifest.json", Manifest);
        Add("gameslist.csv", "\"Game ID\";\"Game Name\"\n\"1001\";\"1942\"\n");
        Add("payloads/1001.json", Payload);
        return path;
    }

    [Fact]
    public async Task IndexPackage_RunsOffThePump_PumpStaysResponsive_SameIndexAsTheSynchronousRun()
    {
        var zip = MakeZip("pkg.zip");
        var catA = new CatalogService(Path.Combine(_tmp, "catA"));
        var catB = new CatalogService(Path.Combine(_tmp, "catB"));
        var direct = new ScreenScraperCachePackageImporter(catA).IndexPackage(zip);

        var r = await RunLikeAHandler(m => m.Run(ActivityKind.Importing, () => new ScreenScraperCachePackageImporter(catB).IndexPackage(zip)));

        Assert.NotEqual(r.PumpThread, r.WorkerThread);
        Assert.Equal(r.PumpThread, r.ContinuationThread);
        Assert.True(r.PumpStayedResponsive);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Importing, null }, r.Rec.Seen);
        Assert.Equal(direct.GameCount, r.Result.GameCount);
        Assert.Equal(direct.MediaCount, r.Result.MediaCount);
        Assert.False(r.Result.WasAlreadyIndexed);
        Assert.Single(catB.LoadCachePackages());
        Assert.Equal(catA.LoadCachePackages().Count, catB.LoadCachePackages().Count);
        // a second registration of the same package is still recognised (no double indexing)
        Assert.True(new ScreenScraperCachePackageImporter(catB).IndexPackage(zip).WasAlreadyIndexed);
        Assert.Single(catB.LoadCachePackages());
    }

    [Fact]
    public async Task IndexPackage_ExceptionOnThePool_PropagatesToTheAwaiter_LikeTheSynchronousCall_AndNothingIsRegistered()
    {
        var bad = Path.Combine(_tmp, "bad.zip");
        File.WriteAllText(bad, "this is not a zip");
        var cat = new CatalogService(Path.Combine(_tmp, "catBad"));
        var rec = new ActivityRecorder();

        var syncEx = Assert.ThrowsAny<Exception>(() => new ScreenScraperCachePackageImporter(cat).IndexPackage(bad));
        var poolEx = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() =>
            rec.Monitor.Run(ActivityKind.Importing, () => new ScreenScraperCachePackageImporter(cat).IndexPackage(bad))));

        Assert.Equal(syncEx.GetType(), poolEx.GetType());
        Assert.Equal(syncEx.Message, poolEx.Message);
        Assert.Empty(cat.LoadCachePackages());
        Assert.True(rec.Idle);
    }

    // ── 1. Move Volume source cleanup: the awaited pool delete (a real directory delete, same semantics) ──────────

    [Fact]
    public async Task MoveCleanupPattern_AwaitedPoolDelete_RemovesTheSourceAndReturnsOnThePump_ExceptionsCatchUnchanged()
    {
        using var pump = new Pump();
        var src = Path.Combine(_tmp, "src-volume");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllBytes(Path.Combine(src, "sub", "a.bin"), new byte[] { 1 });
        var order = new List<string>();

        var threads = await pump.Run(async () =>
        {
            string? cleanupError = null;
            int before = Environment.CurrentManagedThreadId, inside = -1;
            try { await Task.Run(() => { inside = Environment.CurrentManagedThreadId; Directory.Delete(src, recursive: true); }); order.Add("cleanup"); }
            catch (Exception ex) { cleanupError = ex.Message; }
            order.Add("commit");                                        // the commit callback follows the awaited cleanup
            return (before, inside, after: Environment.CurrentManagedThreadId, cleanupError);
        });

        Assert.False(Directory.Exists(src));
        Assert.NotEqual(threads.before, threads.inside);
        Assert.Equal(threads.before, threads.after);
        Assert.Null(threads.cleanupError);
        Assert.Equal(new[] { "cleanup", "commit" }, order);

        // failure: the same catch receives the same exception, and the commit step still runs after it
        var locked = Path.Combine(_tmp, "locked-volume");
        Directory.CreateDirectory(locked);
        using var hold = File.Open(Path.Combine(locked, "busy.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        order.Clear();
        var err = await pump.Run(async () =>
        {
            string? cleanupError = null;
            try { await Task.Run(() => Directory.Delete(locked, recursive: true)); order.Add("cleanup"); }
            catch (Exception ex) { cleanupError = ex.Message; }
            order.Add("commit");
            return cleanupError;
        });
        Assert.NotNull(err);
        Assert.Equal(new[] { "commit" }, order);
        Assert.True(Directory.Exists(locked));
    }

    // ── Wiring that cannot run headless (Move cleanup and cache registration — the two operations kept on the pool) ─────────────────────────────────────────────────────────────────────────

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    private static string Main => File.ReadAllText(RepoFile("MainWindow.axaml.cs")).Replace("\r\n", "\n");

    private static string Region(string src, string start, string end)
    {
        var s = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(s >= 0, start);
        var e = src.IndexOf(end, s + start.Length, StringComparison.Ordinal);
        Assert.True(e > s, end);
        return src.Substring(s, e - s);
    }

    [Fact]
    public void Move_CleanupIsAwaitedOnThePool_BeforeCommitLogAndSummary_AndNothingElseMoved()
    {
        var e = Region(Main, "RunCopyMoveAsync(\n", "    /// <summary>\n    /// Runs the Apply step");
        var cleanup = e.IndexOf("await Task.Run(() => Directory.Delete(srcFolder, recursive: true));", StringComparison.Ordinal);
        var commit  = e.IndexOf("commitAfterMove(cleanupError)", StringComparison.Ordinal);
        var log     = e.IndexOf("File.WriteAllText(logFile", StringComparison.Ordinal);
        var end     = e.IndexOf("moveActivity.Dispose();", StringComparison.Ordinal);
        var summary = e.IndexOf("progDialog.SetCompleted(", StringComparison.Ordinal);
        Assert.True(cleanup > 0 && commit > cleanup && log > commit && end > log && summary > end);
        Assert.Single(Regex.Matches(e, @"Directory\.Delete\("));
        Assert.Equal(1, Regex.Matches(e, @"Task\.Run\(").Count - 1);          // exactly one NEW Task.Run next to the existing copy/verify one

        // the cleanup lives inside the original try/catch that records cleanupError
        var tryStart = e.LastIndexOf("try", cleanup, StringComparison.Ordinal);
        var catchPos = e.IndexOf("cleanupError = ex.Message;", cleanup, StringComparison.Ordinal);
        Assert.True(tryStart > 0 && catchPos > cleanup);
        Assert.Contains("if (errorMessage is null)", e.Substring(e.LastIndexOf("string? cleanupError", cleanup, StringComparison.Ordinal), cleanup - e.LastIndexOf("string? cleanupError", cleanup, StringComparison.Ordinal)));
    }

    [Fact]
    public void Move_CommitCallbackAndProgressDialogLockAreUntouched()
    {
        var h = Region(Main, "private async void OnMoveVolume(", "// ── Volume Resize");
        Assert.Contains("PruneEmptyDirectories(platformDir)", h);                 // callback body unchanged (still sync, still before SetCurrentLocation)
        Assert.True(h.IndexOf("PruneEmptyDirectories(platformDir)", StringComparison.Ordinal) < h.IndexOf("_catalog.SetCurrentLocation(", StringComparison.Ordinal));
        Assert.Contains("void CommitMovedLocation(string? sourceCleanupError)", h);   // still a synchronous Action<string?>
        var dlg = File.ReadAllText(RepoFile("Volumes/WriteVolumeToDiskDialog.axaml.cs"));
        Assert.Contains("if (!CloseButton.IsEnabled) e.Cancel = true;", dlg);    // dialog cannot close until SetCompleted/SetFailed
    }

    // ── FIX-B: Purge Release and Verify All are back to their ORIGINAL synchronous execution ─────────────────────

    [Fact]
    public void PurgeRelease_IsSynchronousAgain_WithItsPurgingScopeAndNoAsyncOnlyGuards()
    {
        var m = Main;
        var p = Region(m, "private async System.Threading.Tasks.Task OnPurgeRelease(", "var summary = new System.Text.StringBuilder();");
        Assert.Contains("var svc    = new Purge.PurgeReleaseService(appRoot, _catalog, Activities.ActivityMonitor.Shared);", p);   // PURGING scope kept (service-level)
        Assert.Contains("var result = svc.Execute(plan);", p);
        Assert.DoesNotContain("Task.Run", p);
        Assert.DoesNotContain("_purgeRunning", m);
        Assert.DoesNotContain("DetailActionsPanel.IsEnabled", m);
        Assert.DoesNotContain("protected override void OnClosing(", m);            // MainWindow never had one
        var bulk = Region(m, "private async void OnLibraryBulkOperations(", "var datLineLabel");
        Assert.DoesNotContain("_purgeRunning", bulk);
        // original order after the run: result dialog → refreshes
        var after = m.Substring(m.IndexOf("var summary = new System.Text.StringBuilder();", StringComparison.Ordinal), 2500);
        Assert.True(after.IndexOf("Purge Complete", StringComparison.Ordinal) < after.IndexOf("RefreshLibraryLeafAfterReleaseMutation(entry.DatLineId);", StringComparison.Ordinal));
    }

    [Fact]
    public void PurgeService_AndBulkPurge_AreUnchanged()
    {
        var svc = File.ReadAllText(RepoFile("Purge/PurgeReleaseService.cs")).Replace("\r\n", "\n");
        Assert.Contains("using var activity = plan.CanExecute ? _activities?.Begin(Arkadia.Activities.ActivityKind.Purging", svc);
        Assert.Contains("File.Delete(la.AbsolutePath)", svc);
        Assert.Contains("_catalog.DeleteVolumeArtifactRow(", svc);
        var bulk = File.ReadAllText(RepoFile("Library/LibraryBulkOperationDialog.axaml.cs"));
        Assert.Contains("result = await Task.Run(", bulk);                         // bulk purge already ran on the pool before FIX-A
    }

    [Fact]
    public void VerifyAll_IsSynchronousAgain_WithItsVerifyingScopeAndTheOriginalToolbarRule()
    {
        var m = Main;
        var v = Region(m, "private async void OnSysVerifyDat(", "var sb = new System.Text.StringBuilder();");
        Assert.Contains("var report = Activities.ActivityMonitor.Shared.Run(Activities.ActivityKind.Verifying,", v);
        Assert.Contains("new Archive.ArchiveOutputBatchValidator(_catalog, _dataDir).ValidateAll()", v);
        Assert.DoesNotContain("Task.Run", v);
        Assert.DoesNotContain("_policyValidationRunning", m);
        Assert.DoesNotContain("SysActVerifyDat.IsEnabled = false", m);
        Assert.Contains("SysActVerifyDat.IsEnabled     = true;", m);               // enable rule as pinned by SystemsToolbarLayoutTests
    }

    [Fact]
    public void VerifyAll_EvidenceForStayingSynchronous_PersistedStateIsConsumedAndNothingGuardsIt()
    {
        // ValidateAll writes form/state/fingerprints per DAT line through a plain UPDATE (no compare-and-set)…
        var cat = File.ReadAllText(RepoFile("DataLayer/CatalogService.cs"));
        var upd = Region(cat.Replace("\r\n", "\n"), "public void UpdateDatLineArchiveOutputValidation(", "cmd.ExecuteNonQuery();");
        Assert.Contains("WHERE id = $id", upd);
        Assert.DoesNotContain("validated_at_utc =", upd.Substring(upd.IndexOf("WHERE", StringComparison.Ordinal)));   // no version/CAS predicate
        // …and an 'unknown' result nulls the fingerprint, which the ingestion gate treats as "legacy → allow".
        var persist = File.ReadAllText(RepoFile("Archive/DatLineArchiveOutputValidationService.cs"));
        Assert.Contains("result.State == ArchiveOutputValidationState.Unknown ? null : result.StructuralFingerprint", persist);
        var gate = File.ReadAllText(RepoFile("Archive/ArchiveIngestionGateEvaluator.cs"));
        Assert.Contains("string.IsNullOrEmpty(storedStructuralFingerprint)", gate);
        // The persisted form/fingerprint are consumed by ingestion and Group planning.
        Assert.Contains("GetDatLineArchiveOutputValidation(datLineId)?.StructuralFingerprint", Main);
        Assert.Contains("GetDatLineArchiveOutputValidation(datLineId)?.Form", Main);
        // No busy flag or lock exists that Configure DAT / Update DAT would honour.
        Assert.DoesNotContain("_policyValidationRunning", Main);
        var cfg = File.ReadAllText(RepoFile("Archive/ArchiveConfigSaveCoordinator.cs"));
        Assert.DoesNotContain("lock (", cfg);
        Assert.DoesNotContain("Mutex", cfg);
    }

    [Fact]
    public void CacheRegistration_RunsOnThePoolBehindAFlag_WithTheOtherActionsDisabled_AndNoCloseMidIndex()
    {
        var d = File.ReadAllText(RepoFile("Providers/ScreenScraperCacheManagerDialog.axaml.cs")).Replace("\r\n", "\n");
        var reg = Region(d, "private async void OnRegisterPackage(", "private async void OnDetach(");
        var guard = reg.IndexOf("if (_registering) return;", StringComparison.Ordinal);
        var set   = reg.IndexOf("_registering = true;", StringComparison.Ordinal);
        var run   = reg.IndexOf("await Task.Run(() => Arkadia.Activities.ActivityMonitor.Shared.Run(", StringComparison.Ordinal);
        var clear = reg.IndexOf("_registering = false;", StringComparison.Ordinal);
        Assert.True(guard > 0 && set > guard && run > set && clear > run);
        Assert.Equal(2, Regex.Matches(reg, @"_registering = false;").Count);       // success path AND failure path
        Assert.Contains("importer.IndexPackage(path)", reg);

        var buttons = Region(d, "private void UpdateButtons()", "// ── Actions");
        foreach (var b in new[] { "VerifyBtn", "DetachBtn", "DeleteBtn", "RegisterBtn", "RefreshBtn" })
            Assert.Contains(b + ".IsEnabled", buttons);
        Assert.Equal(5, Regex.Matches(buttons, @"!_registering").Count);
        Assert.Contains("if (_registering) e.Cancel = true;", d);

        var x = File.ReadAllText(RepoFile("Providers/ScreenScraperCacheManagerDialog.axaml"));
        Assert.Contains("x:Name=\"RegisterBtn\"", x);
        Assert.Contains("x:Name=\"RefreshBtn\"", x);
    }

    [Fact]
    public void BuildVolumeSingleAndTheCores_AreUntouched()
    {
        var m = Main;
        // Build Single still runs synchronously exactly as before FIX-A (explicitly excluded).
        var build = Region(m, "var buildActivity = ", "finally { buildActivity.Dispose(); }");
        Assert.Contains("await ApplyPlanCore(entry, planResult, store)", build);
        Assert.Contains("await BuildVolumeCore(entry)", build);
        Assert.DoesNotContain("Task.Run", build);
        var cores = Region(m, "private System.Threading.Tasks.Task<(int ReleaseCount, int LinkedCount)> ApplyPlanCore(", "// ── Make Volume");
        Assert.DoesNotContain("Task.Run", cores);
        Assert.DoesNotContain("async", cores.Replace("System.Threading.Tasks.Task", ""));

        // Move's copy/verify algorithm and path resolver are untouched.
        var move = Region(m, "RunCopyMoveAsync(\n", "    /// <summary>\n    /// Runs the Apply step");
        Assert.Contains("File.Copy(srcPath, dstPath, overwrite: false);", move);
        Assert.Contains("Volumes.VolumeMoveFileVerifier.VerifyFile(", move);
        var resolver = File.ReadAllText(RepoFile("DataLayer/VolumePathResolver.cs"));
        Assert.True(resolver.IndexOf("// 1. Workspace path", StringComparison.Ordinal) < resolver.IndexOf("// 2. Mounted disk path", StringComparison.Ordinal));
    }

    [Fact]
    public void NoArtificialDelaysDispatcherPumpingOrGlobalLocks_WereAdded()
    {
        var added = new[]
        {
            File.ReadAllText(RepoFile("Providers/ScreenScraperCacheManagerDialog.axaml.cs")),
        };
        foreach (var src in added)
            foreach (var banned in new[] { "Task.Delay", "Task.Yield", "Thread.Sleep", "DoEvents", "RunJobs", "lock (", "Mutex", "SemaphoreSlim", "IsHitTestVisible" })
                Assert.DoesNotContain(banned, src);
        // no window-wide input blocking
        Assert.DoesNotContain("this.IsEnabled", Main);
        Assert.DoesNotContain("Content.IsEnabled", Main);
    }
}
