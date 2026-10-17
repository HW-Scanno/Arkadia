using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Arkadia.Activities;
using Xunit;

namespace Arkadia.Tests.Activities;

/// <summary>
/// GLOBAL-ACTIVITY-INTEGRATION-1 — the Operation → ActivityKind matrix, pinned at the call sites that cannot be
/// constructed headless (MainWindow, provider windows, dialogs), plus the lifecycle rules (scope boundaries relative to
/// summary dialogs) and the "instrumentation only" guarantees. Everything that CAN run without a window is tested
/// functionally in the sibling test classes; these tests only guard the wiring that cannot.
/// </summary>
public sealed class ActivityIntegrationMatrixTests
{
    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    private static string Read(string rel) => File.ReadAllText(RepoFile(rel)).Replace("\r\n", "\n");

    /// <summary>The text of a method body: from its signature to the next member-level marker.</summary>
    private static string Region(string file, string startMarker, string endMarker)
    {
        var src = Read(file);
        var s = src.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(s >= 0, $"{file}: {startMarker}");
        var e = src.IndexOf(endMarker, s + startMarker.Length, StringComparison.Ordinal);
        Assert.True(e > s, $"{file}: {endMarker}");
        return src.Substring(s, e - s);
    }

    // ── Operation → kind matrix at the MainWindow / dialog / window call sites ────────────────────────────────

    public static readonly object[][] Matrix =
    {
        // Operation                       file                                              start marker                                              end marker                                         kind
        new object[] { "Update DAT",        "MainWindow.axaml.cs", "private async System.Threading.Tasks.Task OnUpdateDatLine(", "private ReconciliationResult RunUpdateWork(",              "Updating" },
        new object[] { "Ingest (Single)",   "MainWindow.axaml.cs", "var fileHandling = datLineRecord?.FileHandling",              "await progressDialog.ShowDialog<bool>(this);",           "Ingesting" },
        new object[] { "Ingest (Group)",    "MainWindow.axaml.cs", "private async System.Threading.Tasks.Task ExecuteGroupIngestionAsync(", "// ── Reuse the reviewed plan",                      "Ingesting" },
        new object[] { "Verify DAT",        "MainWindow.axaml.cs", "var dialog  = new DatLineVerifyDialog(info.Name, platformDesc);", "private async System.Threading.Tasks.Task RunVerifyAllDatLine(", "Verifying" },
        new object[] { "Verify Group",      "MainWindow.axaml.cs", "var result = await Ingestion.GroupVerifyOrchestrator.RunAsync(", "if (result.Stopped)",                                  "Group" },
        new object[] { "Verify All policy", "MainWindow.axaml.cs", "private async void OnSysVerifyDat(",                         "var sb = new System.Text.StringBuilder();",             "Verifying" },
        new object[] { "Verify Volume",     "MainWindow.axaml.cs", "private async void OnVerifyVolume(",                         "var svc   = new Volumes.VolumeVerifyService(_catalog);", "Verifying" },
        new object[] { "Repair Volume",     "MainWindow.axaml.cs", "var dlgTask = repairDialog.ShowDialog(this);",               "await dlgTask;",                                        "Repairing" },
        new object[] { "Reabsorb",          "MainWindow.axaml.cs", "var svc = new Volumes.VolumeReabsorbService(",               "result = await Task.Run(() => svc.Execute(",            "Shared" },
        new object[] { "Move Volume",       "MainWindow.axaml.cs", "var moveActivity = ",                                        "moveActivity.Dispose();",                               "Moving" },
        new object[] { "Build Group volume","MainWindow.axaml.cs", "dialog.ExecuteHandler = previewedPlan",                      "var work = System.Threading.Tasks.Task.Run(() =>",      "Creating" },
        new object[] { "Build volume (Single)","MainWindow.axaml.cs", "var buildActivity = ",                                    "finally { buildActivity.Dispose(); }",                  "Creating" },
        new object[] { "Group ingest plan", "MainWindow.axaml.cs", "using var activity = Activities.ActivityMonitor.Shared.Begin(Activities.ActivityKind.Loading, \"Group ingestion plan\")", ";", "Loading" },
        new object[] { "Group volume plan", "MainWindow.axaml.cs", "using var activity = Activities.ActivityMonitor.Shared.Begin(Activities.ActivityKind.Loading, \"Group volume plan\")", ";", "Loading" },
        new object[] { "Dashboard",         "MainWindow.axaml.cs", "using var activity = Activities.ActivityMonitor.Shared.Begin(Activities.ActivityKind.Loading, \"dashboard\")", ";", "Loading" },
        new object[] { "Online scrape",     "MainWindow.axaml.cs", "scrapeActivity = Activities.ActivityMonitor.Shared.Begin(",   ";",                                                    "Scraping" },
        new object[] { "Cache import",      "MainWindow.axaml.cs", "var cacheSummary = await Activities.ActivityMonitor.Shared.RunAsync(", "\"cache import\");",                                   "Importing" },
        new object[] { "AMP import",        "MainWindow.axaml.cs", "var ampSummary = await Activities.ActivityMonitor.Shared.RunAsync(", "\"AMP import\");",                                      "Importing" },
        new object[] { "ARK backup",        "MainWindow.axaml.cs", "using var activity = Activities.ActivityMonitor.Shared.Begin(Activities.ActivityKind.Exporting, \"ARK backup\")", ";",    "Exporting" },
        new object[] { "Validate Integrity","MainWindow.axaml.cs", "report = await Task.Run(() => Activities.ActivityMonitor.Shared.Run(", "\"validate integrity\"));",                                     "Verifying" },
        new object[] { "Image cache",       "MainWindow.axaml.cs", "using var activity = Activities.ActivityMonitor.Shared.Begin(Activities.ActivityKind.Processing, \"image cache\")", ";", "Processing" },
        new object[] { "Bulk scrape",       "MainWindow.axaml.cs", "new CatalogBulkScrapeService(_dataDir, _catalog, _cacheImport,",                         ");",                          "Shared" },
        new object[] { "Create Group DAT",  "MainWindow.axaml.cs", "new GroupDatExecutionService(_catalog, _dataDir,",                                       ");",                          "Shared" },
        new object[] { "Purge Release",     "MainWindow.axaml.cs", "new Purge.PurgeReleaseService(appRoot, _catalog,",                                       ");",                          "Shared" },
        new object[] { "Verify Archive",    "MainWindow.axaml.cs", "new LocalArchive.LocalArchiveVerifyService(appRoot,",                                    ");",                          "Shared" },
        new object[] { "Redump download",   "Providers/RedumpProviderWindow.axaml.cs",     "using var activity = ", ";", "Downloading" },
        new object[] { "TOSEC download",    "Providers/TosecProviderWindow.axaml.cs",      "using var activity = ", ";", "Downloading" },
        new object[] { "Eggman's download", "Providers/EggmansworldProviderWindow.axaml.cs", "using var activity = ", ";", "Downloading" },
        new object[] { "MAME download",     "Providers/MameProviderWindow.axaml.cs",       "var activity = Arkadia.Activities", ";", "Downloading" },
        new object[] { "MAME playlist",     "Providers/MamePlaylistWindow.axaml.cs",       "using var activity = ", ";", "Creating" },
        new object[] { "Cache builder",     "Providers/ScreenScraperCacheBuilderDialog.axaml.cs", "result = await Arkadia.Activities.ActivityMonitor.Shared.RunAsync(", "\"cache builder\");", "Scraping" },
        new object[] { "Cache verify",      "Providers/ScreenScraperCacheManagerDialog.axaml.cs", "result = await Task.Run(() => Arkadia.Activities.ActivityMonitor.Shared.Run(", "\"verify cache package\"));", "Verifying" },
        new object[] { "Cache register",    "Providers/ScreenScraperCacheManagerDialog.axaml.cs", "Task.Run(() => Arkadia.Activities.ActivityMonitor.Shared.Run(", "\"register cache package\"));", "Importing" },
        new object[] { "AMP export",        "Catalog/Amp/AmpExportReportDialog.axaml.cs",  "using var activity = ", ";", "Exporting" },
        new object[] { "AMP registry (UI)", "Providers/AmpLocalPackagesDialog.axaml.cs",   "new AmpLocalRegistryService(dataDir,", ");", "Shared" },
        new object[] { "Append (dialog)",   "Volumes/AppendVolumeDialog.axaml.cs",         "new AppendVolumeService(_catalog,", ");", "Shared" },
        new object[] { "Fillback (dialog)", "Volumes/FillbackVolumeDialog.axaml.cs",       "new VolumeFillbackService(_catalog,", ");", "Shared" },
        new object[] { "Library bulk (UI)", "Library/LibraryBulkOperationDialog.axaml.cs", "new LibraryBulkOperationService(appRoot, catalog,", ");", "Shared" },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void EveryConnectedOperation_IsWiredToItsApprovedKindAtItsCallSite(string operation, string file, string start, string end, string kind)
    {
        _ = operation;
        var src = Read(file);
        var s = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(s >= 0, $"{operation}: marker not found: {start}");
        var e = src.IndexOf(end, s + 1, StringComparison.Ordinal);
        Assert.True(e >= 0, $"{operation}: end marker not found: {end}");
        var region = src.Substring(s, e + end.Length - s);

        if (kind is "Shared")
            Assert.Contains("ActivityMonitor.Shared", region);          // the shared monitor is handed to the service (no private one)
        else if (kind == "Group")
            Assert.Contains("activities: Activities.ActivityMonitor.Shared", region);
        else
            Assert.Contains("ActivityKind." + kind, region);
    }

    // ── Services own their kind ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GroupDats/Execution/GroupDatExecutionService.cs", "Importing")]
    [InlineData("LocalArchive/LocalArchiveVerifyService.cs", "Verifying")]
    [InlineData("LocalArchive/LocalArchiveVerifyService.cs", "Repairing")]
    [InlineData("Volumes/AppendVolumeService.cs", "Appending")]
    [InlineData("Volumes/VolumeFillbackService.cs", "Appending")]
    [InlineData("Volumes/VolumeReabsorbService.cs", "Reabsorbing")]
    [InlineData("Purge/PurgeReleaseService.cs", "Purging")]
    [InlineData("Library/LibraryBulkOperationService.cs", "Purging")]
    [InlineData("Library/LibraryBulkOperationService.cs", "Processing")]
    [InlineData("Catalog/CatalogBulkScrapeService.cs", "Scraping")]
    [InlineData("Catalog/Amp/AmpLocalRegistryService.cs", "Verifying")]
    [InlineData("Catalog/Amp/AmpLocalRegistryService.cs", "Importing")]
    [InlineData("Ingestion/GroupVerifyOrchestrator.cs", "Verifying")]
    public void ServicesDeclareTheirKind_AndTakeTheMonitorAsAnOptionalDependency(string file, string kind)
    {
        var src = Read(file);
        Assert.Contains("ActivityKind." + kind, src);
        Assert.Matches(@"ActivityMonitor\?\s+\w+\s*=\s*null", src);
        Assert.DoesNotContain("new ActivityMonitor(", src);
    }

    // ── Lifecycle boundaries ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MoveVolume_ScopeStartsWithTheCopy_AndEndsAfterCommitAndLog_BeforeTheSummaryIsFinalized()
    {
        var e = Region("MainWindow.axaml.cs", "RunCopyMoveAsync(\n", "    /// <summary>\n    /// Runs the Apply step");
        var begin    = e.IndexOf("var moveActivity = Activities.ActivityMonitor.Shared.Begin(Activities.ActivityKind.Moving", StringComparison.Ordinal);
        var copy     = e.IndexOf("File.Copy(srcPath, dstPath", StringComparison.Ordinal);
        var cleanup  = e.IndexOf("Directory.Delete(srcFolder, recursive: true)", StringComparison.Ordinal);
        var commit   = e.IndexOf("commitAfterMove(cleanupError)", StringComparison.Ordinal);
        var logWrite = e.IndexOf("File.WriteAllText(logFile", StringComparison.Ordinal);
        var end      = e.IndexOf("moveActivity.Dispose();", StringComparison.Ordinal);
        var finalize = e.IndexOf("progDialog.SetCompleted(", StringComparison.Ordinal);
        var awaitDlg = e.IndexOf("await dlgTask;", StringComparison.Ordinal);

        Assert.True(begin > 0 && copy > begin, "scope opens before the first byte is copied");
        Assert.True(end > cleanup && end > commit && end > logWrite, "scope covers cleanup, catalog registration and log");
        Assert.True(finalize > end && awaitDlg > finalize, "the summary dialog is finalized and awaited only AFTER the scope ended");
        Assert.Single(Regex.Matches(e, @"Begin\("));                  // one scope; no per-file or nested registration
    }

    [Fact]
    public void MoveVolume_TransferCodeAndCommitCallbackAreUnchanged()
    {
        var e = Region("MainWindow.axaml.cs", "RunCopyMoveAsync(\n", "    /// <summary>\n    /// Runs the Apply step");
        Assert.Contains("File.Copy(srcPath, dstPath, overwrite: false);", e);
        Assert.Contains("Volumes.VolumeMoveFileVerifier.VerifyFile(", e);
        Assert.Contains("Directory.Delete(srcFolder, recursive: true)", e);
        Assert.Contains("if (errorMessage is null && commitAfterMove is not null)", e);
        Assert.Contains("commitAfterMove(cleanupError)", e);
        var handler = Region("MainWindow.axaml.cs", "private async void OnMoveVolume(", "// ── Volume Resize");
        Assert.DoesNotContain("ActivityKind", handler);                // the handler adds nothing: the engine owns the scope
        Assert.Contains("_catalog.SetCurrentLocation(", handler);
    }

    [Fact]
    public void Ingest_ScopesEndInsideTheWorker_BeforeTheDialogReceivesTheResult()
    {
        var single = Region("MainWindow.axaml.cs", "var fileHandling = datLineRecord?.FileHandling", "await progressDialog.ShowDialog<bool>(this);");
        Assert.True(single.IndexOf("using var activity", StringComparison.Ordinal) < single.IndexOf("RunIngestionWork(", StringComparison.Ordinal));
        Assert.True(single.IndexOf("RunIngestionWork(", StringComparison.Ordinal) < single.IndexOf("progressDialog.SetCompleted(", StringComparison.Ordinal));

        // The repair flow reuses RunIngestionWork: the ingest worker itself must NOT register (REPAIRING is the one kind).
        var worker = Region("MainWindow.axaml.cs", "private IngestionResult RunIngestionWork(", "// Called from the outer release loop in RunIngestionWork.");
        Assert.DoesNotContain("ActivityMonitor", worker);

        var group = Region("MainWindow.axaml.cs", "private async System.Threading.Tasks.Task ExecuteGroupIngestionAsync(", "private ");
        Assert.Single(Regex.Matches(group, @"ActivityKind\.Ingesting"));
        Assert.DoesNotContain("ActivityKind.Verifying", group);
    }

    [Fact]
    public void ScrapeScope_EndsBeforeTheMergeReviewDialog_AndMameBeforeTheCategoryPrompt()
    {
        var scrape = Region("MainWindow.axaml.cs", "scrapeActivity = Activities.ActivityMonitor.Shared.Begin(", "SetScrapeStatus($\"Scrape failed");
        Assert.True(scrape.IndexOf("scrapeActivity.Dispose();", StringComparison.Ordinal) < scrape.IndexOf("new MergeMetadataDialog(", StringComparison.Ordinal));
        Assert.Contains("scrapeActivity?.Dispose();", Read("MainWindow.axaml.cs"));          // finally: covers failure/cancel/"no details"

        var imports = Region("MainWindow.axaml.cs", "var cacheSummary = await", "catch (OperationCanceledException)");
        Assert.True(imports.IndexOf("\"cache import\")", StringComparison.Ordinal) < imports.IndexOf("new MergeMetadataDialog(", StringComparison.Ordinal));

        var mame = Read("Providers/MameProviderWindow.axaml.cs");
        var begin  = mame.IndexOf("var activity = Arkadia.Activities", StringComparison.Ordinal);
        var prompt = mame.IndexOf("await PromptCategoryIniIfNeededAsync();", StringComparison.Ordinal);
        var disposeBeforePrompt = mame.LastIndexOf("activity.Dispose();", prompt, StringComparison.Ordinal);
        Assert.True(begin > 0 && disposeBeforePrompt > begin && disposeBeforePrompt < prompt);
        Assert.Matches(@"finally\s*\{\s*activity\.Dispose\(\);", mame);
    }

    [Fact]
    public void ProviderDownloads_ScopeFollowsTheTask_NotTheWindow()
    {
        foreach (var f in new[] { "Providers/RedumpProviderWindow.axaml.cs", "Providers/TosecProviderWindow.axaml.cs", "Providers/EggmansworldProviderWindow.axaml.cs" })
        {
            var src = Read(f);
            Assert.Single(Regex.Matches(src, @"using var activity = Arkadia\.Activities\.ActivityMonitor\.Shared\.Begin\("));
            // never tied to window lifetime events
            Assert.DoesNotContain("Opened +=", src); Assert.DoesNotContain("OnClosed", src);
            Assert.DoesNotContain("IsVisible", Regex.Match(src, @"using var activity[^\n]*").Value);
        }
        // opening folders / URLs is not an activity
        Assert.DoesNotContain("ActivityMonitor", Read("Providers/NoIntroProviderWindow.axaml.cs"));
    }

    [Fact]
    public void GroupVerify_UsesTheOrchestratorScope_NotAnExtraOneInTheLeafCallback()
    {
        var group = Region("MainWindow.axaml.cs", "private async System.Threading.Tasks.Task VerifyGroupAsync(", "// Maintenance: verify every DAT line's archive output policy");
        Assert.Single(Regex.Matches(group, @"ActivityMonitor\.Shared"));          // only handed to the orchestrator
        Assert.DoesNotContain(".Begin(", group);
    }

    [Fact]
    public void RepairVolume_RegistersOneRepairingScope_AndItsInnerIngestPassIsNotASecondKind()
    {
        var repair = Region("MainWindow.axaml.cs", "var dlgTask = repairDialog.ShowDialog(this);", "await dlgTask;");
        Assert.Single(Regex.Matches(repair, @"ActivityKind\."));
        Assert.Contains("ActivityKind.Repairing", repair);
    }

    // ── Instrumentation only ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoNewActivityKinds_BeyondTheSeventeenApproved()
    {
        var expected = new[]
        {
            "Loading", "Moving", "Ingesting", "Importing", "Verifying", "Appending", "Repairing", "Reabsorbing", "Creating",
            "Updating", "Scraping", "Downloading", "Exporting", "Restoring", "Processing", "Cleaning", "Purging",
        };
        Assert.Equal(expected.OrderBy(x => x), Enum.GetNames<ActivityKind>().OrderBy(x => x));
    }

    [Fact]
    public void RestoringStaysUnused_ArkRestoreIsNotEnabled()
    {
        var offenders = AllSources().Where(f => !f.EndsWith("ActivityKind.cs") && File.ReadAllText(f).Contains("ActivityKind.Restoring")).ToList();
        Assert.Empty(offenders);
        Assert.Contains("Restore Not Available", Read("MainWindow.axaml.cs"));
    }

    [Fact]
    public void OnlyTheSharedMonitorExists_NoWindowOrServiceCreatesItsOwn()
    {
        var offenders = AllSources().Where(f => Regex.IsMatch(File.ReadAllText(f), @"new\s+(Arkadia\.Activities\.)?ActivityMonitor\s*\(")).Select(Path.GetFileName).ToList();
        Assert.Empty(offenders);                                                // nobody constructs a private monitor
        Assert.Contains("public static ActivityMonitor Shared { get; } = new(", Read("Activities/ActivityMonitor.cs"));   // the one singleton
    }

    [Fact]
    public void NoFakeTimersDelaysOrDispatcherPumping_WereAddedToMakeTheStatusVisible()
    {
        var files = new[]
        {
            "MainWindow.axaml.cs", "Ingestion/GroupVerifyOrchestrator.cs", "LocalArchive/LocalArchiveVerifyService.cs",
            "Volumes/AppendVolumeService.cs", "Volumes/VolumeFillbackService.cs", "Volumes/VolumeReabsorbService.cs",
            "Purge/PurgeReleaseService.cs", "Library/LibraryBulkOperationService.cs", "Catalog/Amp/AmpLocalRegistryService.cs",
            "GroupDats/Execution/GroupDatExecutionService.cs",
        };
        foreach (var f in files)
        {
            var src = Read(f);
            foreach (var banned in new[] { "Task.Yield", "DoEvents", "RunJobs", "Thread.Sleep" })
                Assert.DoesNotContain(banned, src);
        }
        // MainWindow's only Task.Delay uses predate this task: none of the instrumented regions use one.
        foreach (var marker in new[] { "using var activity = Activities.", "Activities.ActivityMonitor.Shared.Begin(" })
            foreach (Match m in Regex.Matches(Read("MainWindow.axaml.cs"), Regex.Escape(marker)))
                Assert.DoesNotContain("Task.Delay", Read("MainWindow.axaml.cs").Substring(m.Index, 120));
    }

    [Fact]
    public void InstrumentedServices_AddNoFileSystemOrDatabaseCalls_OnlyAScopeLine()
    {
        // The monitor lines are the ONLY additions: every instrumented service touches the filesystem exactly as before,
        // so a scope line must never contain I/O.
        foreach (var f in new[] { "LocalArchive/LocalArchiveVerifyService.cs", "Volumes/AppendVolumeService.cs", "Volumes/VolumeFillbackService.cs",
                                  "Volumes/VolumeReabsorbService.cs", "Purge/PurgeReleaseService.cs", "Library/LibraryBulkOperationService.cs" })
            foreach (var line in Read(f).Split('\n').Where(l => l.Contains("_activities?.Begin(") || l.Contains("activities?.Begin(")))
                foreach (var io in new[] { "File.", "Directory.", "Path.", "Sha", "Sqlite", "Store" })
                    Assert.DoesNotContain(io, line);
    }

    [Fact]
    public void UnsupportedAndNotImplementedWorkflows_AreNotInstrumented()
    {
        // Update Group DAT has no execution path today: no activity may be invented for it.
        var main = Read("MainWindow.axaml.cs");
        Assert.Contains("Execution is available for Create only", Read("GroupDats/GroupDatReconciliationDialog.axaml.cs") + main + string.Concat(AllSources().Where(f => f.Contains("GroupDats")).Select(File.ReadAllText)));
        Assert.DoesNotContain("ActivityKind", Read("GroupDats/GroupDatReconciliationDialog.axaml.cs"));
        // Clean Empty Folders keeps its own CLEANING scopes, untouched.
        var cleaner = Read("Maintenance/EmptyFolderCleaner.cs");
        Assert.Equal(2, Regex.Matches(cleaner, @"_activities\?\.Begin\(ActivityKind\.Cleaning").Count);
    }

    private static string[] AllSources()
        => Directory.EnumerateFiles(RepoFile("."), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && !f.Contains(@"\Arkadia.Tests\")).ToArray();
}
