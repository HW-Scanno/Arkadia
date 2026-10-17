using System;
using System.Collections.Generic;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Arkadia.Activities;
using Arkadia.Maintenance;
using Xunit;

namespace Arkadia.Tests.Maintenance;

/// <summary>
/// CLEAN-EMPTY-FOLDERS-1. Every test works in its own throw-away temp tree (never the app folder, never real data).
/// The central guarantees: no file is ever deleted, the three roots survive, links are never followed, and every
/// candidate is re-validated right before its non-recursive delete.
/// </summary>
public sealed class EmptyFolderCleanerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ark-clean-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = new();

    public EmptyFolderCleanerTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        foreach (var j in _junctions) { try { Directory.Delete(j, false); } catch { } }   // remove the LINK only, never its target
        try { ClearReadOnly(_base); Directory.Delete(_base, true); } catch { }
    }

    private static void ClearReadOnly(string dir)
    {
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(d, FileAttributes.Directory); } catch { }
    }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine(new[] { _base }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    private static string File0(string dir, string name, byte[]? data = null)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, data ?? new byte[] { 1, 2, 3 });
        return p;
    }

    private IReadOnlyList<CleanupRoot> Roots() => EmptyFolderCleaner.ManagedRoots(_base);

    private static Dictionary<string, string> Snapshot(string dir)
        => Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    /// <summary>mklink /J (no elevation needed). Returns false if the platform refuses.</summary>
    private bool TryJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            if (p.ExitCode != 0 || !Directory.Exists(link)) return false;
            _junctions.Add(link);
            return true;
        }
        catch { return false; }
    }

    // ── Roots ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ManagedRoots_AreStagingArchiveSource_UnderTheBaseFolder()
    {
        var r = Roots();
        Assert.Equal(new[] { "Staging", "Archive", "Source" }, r.Select(x => x.Label));
        Assert.Equal(Path.Combine(_base, "staging"), r[0].Path);
        Assert.Equal(Path.Combine(_base, "archive"), r[1].Path);
        Assert.Equal(Path.Combine(_base, "source"),  r[2].Path);
    }

    [Fact]
    public void MissingRoots_AreReported_NothingIsCreated()
    {
        var scan = new EmptyFolderCleaner().Scan(Roots());
        Assert.All(scan.Roots, r => { Assert.False(r.RootExists); Assert.Empty(r.Candidates); });
        Assert.False(Directory.Exists(Path.Combine(_base, "staging")));
        Assert.False(Directory.Exists(Path.Combine(_base, "archive")));
        Assert.False(Directory.Exists(Path.Combine(_base, "source")));
    }

    [Fact]
    public void RootsThemselves_AreNeverCandidates_EvenWhenEmpty_AndSurviveTheRun()
    {
        foreach (var n in new[] { "staging", "archive", "source" }) Dir(n);
        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());
        Assert.Equal(0, scan.CandidateCount);
        var res = c.Execute(scan);
        Assert.Empty(res.Removed);
        foreach (var n in new[] { "staging", "archive", "source" }) Assert.True(Directory.Exists(Path.Combine(_base, n)));
    }

    [Fact]
    public void InvalidRoots_AreRefused_RelativeDriveRootAndOverlapping()
    {
        var c = new EmptyFolderCleaner();
        var drive = Path.GetPathRoot(_base)!;
        var scan = c.Scan(new[]
        {
            new CleanupRoot("Rel", "relative\\folder"),
            new CleanupRoot("Drv", drive),
            new CleanupRoot("A", Path.Combine(_base, "a")),
            new CleanupRoot("B", Path.Combine(_base, "a", "b")),
        });
        Assert.All(scan.Roots, r => Assert.NotNull(r.RootProblem));
        Assert.Equal(0, scan.CandidateCount);
    }

    // ── Scan and removal ─────────────────────────────────────────────────────────

    [Fact]
    public void NestedEmptyDirectories_AreRemovedBottomUp_ParentsIncluded_RootsKept()
    {
        var deep = Dir("archive", "c64", "line-a", "x", "y", "z");
        Dir("staging", "nes", "old");
        Dir("source", "snes");
        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());

        var arch = scan.Roots.Single(r => r.Root.Label == "Archive").Candidates.Select(x => x.Path).ToList();
        Assert.Equal(5, arch.Count);                                        // z, y, x, line-a, c64
        Assert.Equal(deep, arch[0]);                                        // deepest first
        Assert.True(arch.IndexOf(Path.Combine(_base, "archive", "c64", "line-a", "x")) > arch.IndexOf(Path.Combine(_base, "archive", "c64", "line-a", "x", "y")));
        Assert.Equal(8, scan.CandidateCount);

        var res = c.Execute(scan);
        Assert.Equal(8, res.Removed.Count);
        Assert.Empty(res.Skipped); Assert.Empty(res.Failed); Assert.False(res.Cancelled);
        foreach (var n in new[] { "staging", "archive", "source" })
        {
            Assert.True(Directory.Exists(Path.Combine(_base, n)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_base, n)));
        }
    }

    [Fact]
    public void NonEmptyDirectories_AndTheirAncestors_AreKept_FilesUntouched()
    {
        var keep = Dir("archive", "c64", "line-a");
        File0(keep, "game.zip");
        Dir("archive", "c64", "line-a", "empty-child");
        Dir("archive", "c64", "line-b");                                     // empty sibling → removable
        var before = Snapshot(_base);

        var c = new EmptyFolderCleaner();
        var res = c.Execute(c.Scan(Roots()));

        Assert.Equal(2, res.Removed.Count);                                  // empty-child and line-b
        Assert.True(Directory.Exists(keep));
        Assert.True(Directory.Exists(Path.Combine(_base, "archive", "c64")));
        Assert.Equal(before, Snapshot(_base));                               // byte-identical files
    }

    [Fact]
    public void ZeroByteAndHiddenFiles_KeepTheirFolder()
    {
        var a = Dir("staging", "p", "zero");
        File.WriteAllBytes(Path.Combine(a, "z.bin"), Array.Empty<byte>());
        var b = Dir("staging", "p", "hidden");
        var hf = File0(b, ".keep");
        File.SetAttributes(hf, FileAttributes.Hidden);
        var d = Dir("staging", "p", "system");
        var sf = File0(d, "desktop.ini");
        File.SetAttributes(sf, FileAttributes.Hidden | FileAttributes.System);

        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());
        Assert.Equal(0, scan.CandidateCount);
        var res = c.Execute(scan);
        Assert.Empty(res.Removed);
        Assert.True(File.Exists(Path.Combine(a, "z.bin")) && File.Exists(hf) && File.Exists(sf));
    }

    [Fact]
    public void HiddenAndSystemDirectories_AreLeftAlone_AndReported()
    {
        var parent = Dir("source", "p");
        var hidden = Dir("source", "p", "h");
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
        Dir("source", "p", "plain");

        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());
        var src = scan.Roots.Single(r => r.Root.Label == "Source");
        Assert.Equal(new[] { Path.Combine(parent, "plain") }, src.Candidates.Select(x => x.Path));
        Assert.Contains(src.Notes, n => n.Path == hidden);
        c.Execute(scan);
        Assert.True(Directory.Exists(hidden));
        Assert.True(Directory.Exists(parent));                               // the hidden dir keeps its parent
    }

    [Fact]
    public void Junctions_AreNeverFollowedNorRemoved_AndTheirTargetIsUntouched()
    {
        var outside = Path.Combine(_base, "outside-tree");
        Directory.CreateDirectory(Path.Combine(outside, "empty-a", "empty-b"));
        var keepFile = File0(outside, "precious.bin");
        var host = Dir("archive", "c64");
        var link = Path.Combine(host, "link");
        if (!TryJunction(link, outside)) return;                             // platform refused: nothing to test

        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());
        Assert.Equal(0, scan.CandidateCount);                                // outside's empty dirs are NOT reachable
        Assert.Contains(scan.Roots.Single(r => r.Root.Label == "Archive").Notes, n => n.Path == link);
        var res = c.Execute(scan);

        Assert.Empty(res.Removed);
        Assert.True(Directory.Exists(link));                                 // link kept → parent kept
        Assert.True(Directory.Exists(host));
        Assert.True(Directory.Exists(Path.Combine(outside, "empty-a", "empty-b")));
        Assert.True(File.Exists(keepFile));
    }

    [Fact]
    public void ARootThatIsAJunction_IsNotScanned()
    {
        var outside = Dir("elsewhere");
        Directory.CreateDirectory(Path.Combine(outside, "empty"));
        var rootPath = Path.Combine(_base, "archive");
        if (!TryJunction(rootPath, outside)) return;

        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());
        var r = scan.Roots.Single(x => x.Root.Label == "Archive");
        Assert.NotNull(r.RootProblem);
        Assert.Empty(r.Candidates);
        c.Execute(scan);
        Assert.True(Directory.Exists(Path.Combine(outside, "empty")));
    }

    [Fact]
    public void Symlinks_WhenTheSystemAllowsThem_AreNeverFollowedNorRemoved()
    {
        var outside = Dir("outside-sym");
        Directory.CreateDirectory(Path.Combine(outside, "e"));
        var host = Dir("staging", "p");
        var link = Path.Combine(host, "sl");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch { return; }                                                     // needs privilege/developer mode

        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());
        Assert.Equal(0, scan.CandidateCount);
        c.Execute(scan);
        Assert.True(Directory.Exists(Path.Combine(outside, "e")));
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        Directory.Delete(link, false);
    }

    // ── Last-moment changes ──────────────────────────────────────────────────────

    [Fact]
    public void ADirectoryThatGainedAFileAfterTheScan_IsSkipped_AndTheFileIsKept()
    {
        var a = Dir("archive", "c64", "a");
        var b = Dir("archive", "c64", "b");
        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());

        var late = File0(a, "arrived.zip");
        var res = c.Execute(scan);

        Assert.True(File.Exists(late));
        Assert.True(Directory.Exists(a));
        Assert.False(Directory.Exists(b));
        Assert.Contains(res.Skipped, n => n.Path == a);
        Assert.Contains(res.Skipped, n => n.Path == Path.Combine(_base, "archive", "c64"));   // parent no longer empty either
        Assert.Empty(res.Failed);
    }

    [Fact]
    public void ADirectoryThatGainedASubfolderWithAFile_KeepsEverythingBelow()
    {
        Dir("source", "p", "a");
        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());
        var sub = Dir("source", "p", "a", "new");
        var f = File0(sub, "x.bin");
        var res = c.Execute(scan);
        Assert.True(File.Exists(f));
        Assert.Empty(res.Removed);
    }

    [Fact]
    public void ACandidateThatDisappeared_IsSkipped_NotFailed()
    {
        var a = Dir("staging", "p", "a");
        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());
        Directory.Delete(a);
        var res = c.Execute(scan);
        Assert.Contains(res.Skipped, n => n.Path == a);
        Assert.Empty(res.Failed);
    }

    [Fact]
    public void ACandidateReplacedByAJunctionAfterTheScan_IsNotTouched()
    {
        var outside = Dir("outside-swap");
        Directory.CreateDirectory(Path.Combine(outside, "inner"));
        var a = Dir("archive", "p", "a");
        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());
        Directory.Delete(a);
        if (!TryJunction(a, outside)) return;

        var res = c.Execute(scan);
        Assert.True(Directory.Exists(a));
        Assert.True(Directory.Exists(Path.Combine(outside, "inner")));
        Assert.DoesNotContain(a, res.Removed);
    }

    [Fact]
    public void ACandidateOutsideItsRoot_OrEqualToIt_IsRejected()
    {
        var stray = Dir("not-managed", "empty");
        var rootDir = Dir("archive");
        var twin = Dir("archive-twin");
        var archive = Roots().Single(r => r.Label == "Archive");
        var forged = new EmptyFolderScan(new[]
        {
            new EmptyFolderRootScan(archive, true, null,
                new[] { new EmptyFolderCandidate("Archive", stray), new EmptyFolderCandidate("Archive", rootDir),
                        new EmptyFolderCandidate("Archive", twin) },
                Array.Empty<CleanupNote>()),
        }, false);

        var res = new EmptyFolderCleaner().Execute(forged);
        Assert.Empty(res.Removed);
        Assert.Equal(3, res.Skipped.Count);
        Assert.True(Directory.Exists(stray)); Assert.True(Directory.Exists(rootDir)); Assert.True(Directory.Exists(twin));
    }

    // ── Filesystem errors ────────────────────────────────────────────────────────

    [Fact]
    public void AFolderTheSystemRefusesToDelete_IsReportedAsFailed_AndTheRunContinues()
    {
        var locked = Dir("archive", "p", "locked");
        var fine = Dir("archive", "p", "fine");
        File.SetAttributes(locked, FileAttributes.Directory | FileAttributes.ReadOnly);

        var c = new EmptyFolderCleaner();
        var res = c.Execute(c.Scan(Roots()));
        try
        {
            if (Directory.Exists(locked))                                    // Windows: a read-only directory is refused
            {
                Assert.Contains(res.Failed, n => n.Path == locked);
                Assert.Contains(res.Skipped, n => n.Path == Path.Combine(_base, "archive", "p"));
            }
            Assert.False(Directory.Exists(fine));                            // the other folder was still handled
        }
        finally { if (Directory.Exists(locked)) File.SetAttributes(locked, FileAttributes.Directory); }
    }

    // ── Cancellation ─────────────────────────────────────────────────────────────

    [Fact]
    public void CancelledScan_IsReportedAsCancelled_AndChangesNothing()
    {
        Dir("archive", "p", "a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var scan = new EmptyFolderCleaner().Scan(Roots(), cts.Token);
        Assert.True(scan.Cancelled);
        Assert.True(Directory.Exists(Path.Combine(_base, "archive", "p", "a")));
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }

    [Fact]
    public void CancelledRemoval_StopsBetweenFolders_AndCountsTheRest()
    {
        for (int i = 0; i < 5; i++) Dir("archive", "p" + i);
        var c = new EmptyFolderCleaner();
        var scan = c.Scan(Roots());
        using var cts = new CancellationTokenSource();
        var res = c.Execute(scan, cts.Token, new SyncProgress<EmptyFolderProgress>(p => { if (p.Done == 2 && p.CurrentPath.Length > 0) cts.Cancel(); }));

        Assert.True(res.Cancelled);
        Assert.Equal(2, res.Removed.Count);
        Assert.Equal(3, res.NotProcessed);
        Assert.Equal(3, Directory.GetDirectories(Path.Combine(_base, "archive")).Length);
    }

    // ── Never deletes files ──────────────────────────────────────────────────────

    [Fact]
    public void ARealisticMixedTree_LosesNoFile()
    {
        var rng = new Random(7);
        for (int i = 0; i < 40; i++)
        {
            var d = Dir(new[] { "staging", "archive", "source" }[i % 3], "p" + (i % 4), "l" + i, "n" + (i % 3));
            if (rng.Next(3) == 0) File0(d, "f" + i + ".bin", new byte[rng.Next(0, 5)]);
            if (rng.Next(5) == 0) { var h = File0(d, "h" + i); File.SetAttributes(h, FileAttributes.Hidden); }
        }
        var before = Snapshot(_base);
        var c = new EmptyFolderCleaner();
        var res = c.Execute(c.Scan(Roots()));
        Assert.True(res.Removed.Count > 0);
        Assert.Equal(before, Snapshot(_base));
    }

    [Fact]
    public void TheCleanerSource_UsesOnlyNonRecursiveDirectoryDelete()
    {
        var src = string.Join(Environment.NewLine, File.ReadAllLines(RepoFile("Maintenance/EmptyFolderCleaner.cs")).Where(l => !l.TrimStart().StartsWith("//")));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"Directory\.Delete\("));
        Assert.Contains("Directory.Delete(path, recursive: false)", src);
        foreach (var banned in new[] { "File.Delete", "recursive: true", ", true)", "Directory.Move", "File.Move", "File.Copy", "File.Create", "File.Write", "SetAttributes", "SHA1", "SHA256", "Process.Start", "Sqlite" })
            Assert.DoesNotContain(banned, src);
    }

    // ── Activity scope ───────────────────────────────────────────────────────────

    [Fact]
    public void Scan_And_Execute_HoldACleaningScope_OnlyWhileWorking_AndReleaseIt()
    {
        Dir("archive", "p", "a"); Dir("archive", "p", "b");
        var m = new ActivityMonitor();
        var c = new EmptyFolderCleaner(m);
        Assert.True(m.IsIdle);

        var scan = c.Scan(Roots());
        Assert.True(m.IsIdle);                                               // preview: READY

        ActivityKind? during = null;
        var res = c.Execute(scan, default, new SyncProgress<EmptyFolderProgress>(_ => during ??= m.Foreground));
        Assert.Equal(ActivityKind.Cleaning, during);
        Assert.True(m.IsIdle);                                               // summary: READY
        Assert.Equal(3, res.Removed.Count);
    }

    [Fact]
    public void TheCleaningScope_IsReleased_OnCancellationAndOnFailure()
    {
        for (int i = 0; i < 3; i++) Dir("archive", "q" + i);
        var m = new ActivityMonitor();
        var c = new EmptyFolderCleaner(m);

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Assert.True(c.Scan(Roots(), cts.Token).Cancelled);
            Assert.True(m.IsIdle);
        }

        var scan = c.Scan(Roots());
        using (var cts = new CancellationTokenSource())
        {
            var res = c.Execute(scan, cts.Token, new SyncProgress<EmptyFolderProgress>(p => { if (p.Done == 1 && p.CurrentPath.Length > 0) cts.Cancel(); }));
            Assert.True(res.Cancelled);
            Assert.True(m.IsIdle);
        }

        Assert.Throws<InvalidOperationException>(() =>
            c.Execute(scan, default, new SyncProgress<EmptyFolderProgress>(_ => throw new InvalidOperationException("boom"))));
        Assert.True(m.IsIdle);                                               // even when the work throws
    }

    [Fact]
    public void ConflictingActivity_OnlyLoadingIsTolerated()
    {
        Assert.Null(new EmptyFolderCleaner().ConflictingActivity());
        var m = new ActivityMonitor();
        var c = new EmptyFolderCleaner(m);
        Assert.Null(c.ConflictingActivity());
        using (m.Begin(ActivityKind.Loading)) Assert.Null(c.ConflictingActivity());

        foreach (var k in Enum.GetValues<ActivityKind>().Where(k => k != ActivityKind.Loading))
            using (m.Begin(k)) Assert.NotNull(c.ConflictingActivity());      // includes another Cleaning
    }

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }
}
