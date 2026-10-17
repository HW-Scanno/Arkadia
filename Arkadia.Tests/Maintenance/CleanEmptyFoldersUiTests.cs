using System;
using System.IO;
using System.Linq;
using Arkadia.Activities;
using Arkadia.Maintenance;
using Xunit;

namespace Arkadia.Tests.Maintenance;

/// <summary>
/// CLEAN-EMPTY-FOLDERS-1: the report text and the structure of the Settings entry + dialog. The window cannot be
/// constructed headless, so its wiring is pinned on the source (the behaviour itself is covered by the cleaner tests
/// and was checked in an isolated app instance).
/// </summary>
public sealed class CleanEmptyFoldersUiTests
{
    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    private static string Read(string rel) => File.ReadAllText(RepoFile(rel)).Replace("\r\n", "\n");

    // ── Settings entry ───────────────────────────────────────────────────────────

    [Fact]
    public void Settings_HasAGeneralCard_WithTheCleanEmptyFoldersButton()
    {
        var x = Read("MainWindow.axaml");
        var settings = x.Substring(x.IndexOf("x:Name=\"ViewSettings\"", StringComparison.Ordinal));
        var general = settings.IndexOf("Text=\"GENERAL\"", StringComparison.Ordinal);
        var button  = settings.IndexOf("x:Name=\"CleanEmptyFoldersButton\"", StringComparison.Ordinal);
        var logging = settings.IndexOf("Text=\"LOGGING\"", StringComparison.Ordinal);
        Assert.True(general > 0 && button > general && button < logging, "the button sits in the GENERAL card");
        Assert.Contains("Content=\"Clean Empty Folders\"", x);
        Assert.Contains("Click=\"OnCleanEmptyFolders\"", x);
    }

    [Fact]
    public void Handler_OpensTheDialog_WithTheManagedRootsOfTheAppFolder_AndTheSharedMonitor()
    {
        var c = Read("MainWindow.axaml.cs");
        var start = c.IndexOf("private async void OnCleanEmptyFolders(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = c.Substring(start, c.IndexOf("// ── Integrity Validation", start, StringComparison.Ordinal) - start);
        Assert.Contains("EmptyFolderCleaner.ManagedRoots(AppContext.BaseDirectory)", body);
        Assert.Contains("ActivityMonitor.Shared", body);
        Assert.Contains("ShowDialog(this)", body);
        // the handler itself touches no folder and starts no scan: that happens inside the dialog, after it opened
        foreach (var banned in new[] { "Directory.", "File.", ".Scan(", ".Execute(", ".Begin(" })
            Assert.DoesNotContain(banned, body);
    }

    [Fact]
    public void NothingRunsAutomatically_TheCleanerIsReferencedOnlyFromTheSettingsHandlerAndItsDialog()
    {
        var offenders = Directory.EnumerateFiles(RepoFile("."), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && !f.Contains(@"\Arkadia.Tests\") && !f.Contains(@"\Maintenance\"))
            .Where(f => File.ReadAllText(f).Contains("EmptyFolderCleaner"))
            .Select(f => Path.GetFileName(f))
            .ToList();
        Assert.Equal(new[] { "MainWindow.axaml.cs" }, offenders);
    }

    // ── Dialog: states and the Cleaning scope ownership ──────────────────────────

    [Fact]
    public void Dialog_NeverOpensAnActivityScopeItself_TheCleanerOwnsItWhileWorking()
    {
        var d = Read("Maintenance/CleanEmptyFoldersDialog.axaml.cs");
        Assert.DoesNotContain(".Begin(", d);
        Assert.DoesNotContain("ActivityMonitor", d);
        var cleaner = Read("Maintenance/EmptyFolderCleaner.cs");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(cleaner, @"_activities\?\.Begin\(ActivityKind\.Cleaning").Count);   // scan + remove
    }

    [Fact]
    public void Dialog_RemovesOnlyAfterTheExplicitConfirmButton_AndRechecksConflicts()
    {
        var d = Read("Maintenance/CleanEmptyFoldersDialog.axaml.cs");
        var scan   = d.IndexOf("_cleaner.Scan(", StringComparison.Ordinal);
        var exec   = d.IndexOf("_cleaner.Execute(", StringComparison.Ordinal);
        var remove = d.IndexOf("private async void OnRemove(", StringComparison.Ordinal);
        Assert.True(scan > 0 && remove > scan && exec > remove);             // Execute is only reachable from OnRemove
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(d, @"_cleaner\.Execute\("));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(d, @"ConflictingActivity\(\)").Count);   // before scan and before removal
        Assert.Contains("Phase.Preview", d);
        Assert.Contains("OnClosing", d);                                      // a running removal is cancelled, not abandoned

        var x = Read("Maintenance/CleanEmptyFoldersDialog.axaml");
        Assert.Contains("Click=\"OnRemove\"", x);
        Assert.Contains("Click=\"OnCancel\"", x);
        Assert.Contains("IsReadOnly=\"True\"", x);
    }

    // ── Report text ──────────────────────────────────────────────────────────────

    private static EmptyFolderScan SampleScan(int archiveCandidates, bool withNote = false)
    {
        var archive = new CleanupRoot("Archive", @"C:\app\archive");
        var staging = new CleanupRoot("Staging", @"C:\app\staging");
        var cands = Enumerable.Range(0, archiveCandidates).Select(i => new EmptyFolderCandidate("Archive", $@"C:\app\archive\p\d{i}")).ToList();
        var notes = withNote ? new[] { new CleanupNote(@"C:\app\archive\p\lnk", "Link (symlink/junction/mount point): not followed, not removed.") } : Array.Empty<CleanupNote>();
        return new EmptyFolderScan(new[]
        {
            new EmptyFolderRootScan(staging, false, null, Array.Empty<EmptyFolderCandidate>(), Array.Empty<CleanupNote>()),
            new EmptyFolderRootScan(archive, true, null, cands, notes),
        }, false);
    }

    [Fact]
    public void Headline_CountsAndPluralises()
    {
        Assert.Equal("No empty folders found.", EmptyFolderReport.Headline(SampleScan(0)));
        Assert.Contains("1 empty folder can be removed", EmptyFolderReport.Headline(SampleScan(1)));
        Assert.Contains("3 empty folders can be removed", EmptyFolderReport.Headline(SampleScan(3)));
        Assert.Contains("No files will be deleted", EmptyFolderReport.Headline(SampleScan(3)));
    }

    [Fact]
    public void Preview_GroupsByRoot_WithRelativePathsCountsAndNotes()
    {
        var t = EmptyFolderReport.Preview(SampleScan(2, withNote: true));
        Assert.Contains("STAGING", t);
        Assert.Contains("Folder not present", t);
        Assert.Contains("ARCHIVE  —  C:\\app\\archive", t);
        Assert.Contains("2 empty folders", t);
        Assert.Contains("    p\\d0", t);
        Assert.Contains("Left untouched (1)", t);
        Assert.True(t.IndexOf("STAGING", StringComparison.Ordinal) < t.IndexOf("ARCHIVE", StringComparison.Ordinal));
    }

    [Fact]
    public void Preview_TruncatesLongListsButNeverTheCounts()
    {
        var t = EmptyFolderReport.Preview(SampleScan(EmptyFolderReport.MaxListed + 25));
        Assert.Contains($"{EmptyFolderReport.MaxListed + 25} empty folders", t);
        Assert.Contains("… and 25 more", t);
    }

    [Fact]
    public void Summary_ReportsRemovedSkippedFailedAndCancellation()
    {
        var done = new EmptyFolderCleanupResult(new[] { "a", "b" }, new[] { new CleanupNote("c", "No longer empty; left untouched.") },
            new[] { new CleanupNote("d", "Access denied") }, cancelled: false, notProcessed: 0);
        var t = EmptyFolderReport.Summary(done);
        Assert.Contains("Cleanup finished.", t);
        Assert.Contains("Removed:  2", t); Assert.Contains("Skipped:  1", t); Assert.Contains("Failed:   1", t);
        Assert.Contains("c  —  No longer empty", t); Assert.Contains("d  —  Access denied", t);

        var stopped = new EmptyFolderCleanupResult(new[] { "a" }, Array.Empty<CleanupNote>(), Array.Empty<CleanupNote>(), cancelled: true, notProcessed: 4);
        var s = EmptyFolderReport.Summary(stopped);
        Assert.Contains("Cancelled", s);
        Assert.Contains("Not processed: 4", s);
    }
}
