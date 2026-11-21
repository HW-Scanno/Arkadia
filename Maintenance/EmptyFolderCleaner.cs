using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Activities;

namespace Arkadia.Maintenance;

/// <summary>One managed root the cleaner may look into (e.g. "Staging" → <c>&lt;app&gt;\staging</c>).</summary>
public sealed record CleanupRoot(string Label, string Path);

/// <summary>An empty directory that the cleaner would remove. <see cref="Path"/> is always strictly inside its root.</summary>
public sealed record EmptyFolderCandidate(string RootLabel, string Path);

/// <summary>Something the cleaner deliberately left alone (or could not remove), with the reason.</summary>
public sealed record CleanupNote(string Path, string Reason);

/// <summary>Scan result of one root. <see cref="Candidates"/> are ordered deepest first (children before their parents).</summary>
public sealed class EmptyFolderRootScan
{
    public EmptyFolderRootScan(CleanupRoot root, bool rootExists, string? rootProblem,
        IReadOnlyList<EmptyFolderCandidate> candidates, IReadOnlyList<CleanupNote> notes)
    {
        Root = root; RootExists = rootExists; RootProblem = rootProblem; Candidates = candidates; Notes = notes;
    }

    public CleanupRoot Root { get; }
    /// <summary>False when the root folder is not there at all (nothing to do, nothing created).</summary>
    public bool RootExists { get; }
    /// <summary>Non-null when the root was NOT scanned (e.g. it is a link or an invalid root).</summary>
    public string? RootProblem { get; }
    public IReadOnlyList<EmptyFolderCandidate> Candidates { get; }
    /// <summary>Entries that were not followed / not touched (links, hidden or system folders, unreadable folders).</summary>
    public IReadOnlyList<CleanupNote> Notes { get; }
}

public sealed class EmptyFolderScan
{
    public EmptyFolderScan(IReadOnlyList<EmptyFolderRootScan> roots, bool cancelled) { Roots = roots; Cancelled = cancelled; }

    public IReadOnlyList<EmptyFolderRootScan> Roots { get; }
    public bool Cancelled { get; }
    public int CandidateCount => Roots.Sum(r => r.Candidates.Count);
}

public sealed class EmptyFolderCleanupResult
{
    public EmptyFolderCleanupResult(IReadOnlyList<string> removed, IReadOnlyList<CleanupNote> skipped,
        IReadOnlyList<CleanupNote> failed, bool cancelled, int notProcessed)
    {
        Removed = removed; Skipped = skipped; Failed = failed; Cancelled = cancelled; NotProcessed = notProcessed;
    }

    public IReadOnlyList<string> Removed { get; }
    public IReadOnlyList<CleanupNote> Skipped { get; }
    public IReadOnlyList<CleanupNote> Failed { get; }
    public bool Cancelled { get; }
    /// <summary>Candidates never reached because the run was cancelled.</summary>
    public int NotProcessed { get; }
}

/// <summary>Progress of <see cref="EmptyFolderCleaner.Execute"/>: <paramref name="Done"/> of <paramref name="Total"/> candidates handled.</summary>
public readonly record struct EmptyFolderProgress(int Done, int Total, string CurrentPath);

/// <summary>
/// CLEAN-EMPTY-FOLDERS-1: removes EMPTY directories (and only empty directories) from Arkadia's four managed
/// roots — <c>staging</c>, <c>archive</c>, <c>source</c> and <c>data\media</c> under the application folder. Manual,
/// on-demand, UI-free and conservative:
///
/// <list type="bullet">
/// <item>It never deletes a file — hidden, system or zero-byte files all count as content and keep their folder.
/// The only deletion primitive used is <c>Directory.Delete(path, recursive: false)</c>, which the OS refuses for any
/// directory that has content.</item>
/// <item>The four roots themselves are protected: they are never candidates, never deleted, and never created.</item>
/// <item>Symbolic links, junctions and mount points (any reparse point) are never followed and never removed; they
/// count as content of their parent. Hidden and system directories are left alone too.</item>
/// <item>Each candidate is re-validated immediately before deletion (still inside its root, no link on the way,
/// still empty); anything that changed in the meantime is skipped, not forced.</item>
/// <item>No database, volume, manifest or catalog data is read or written; offline volumes are not part of the roots.</item>
/// </list>
///
/// <para>While the scan or the removal is actually running a <see cref="ActivityKind.Cleaning"/> scope is open on the
/// optional <see cref="ActivityMonitor"/>; it is released on completion, cancellation and failure alike. Nothing is
/// registered while the user is only looking at the preview or the summary.</para>
/// </summary>
public sealed class EmptyFolderCleaner
{
    public const string StagingLabel = "Staging", ArchiveLabel = "Archive", SourceLabel = "Source", MediaLabel = "Media";

    private const int MaxDepth = 256;

    // AttributesToSkip = 0: hidden and system entries are part of the picture, so a hidden file keeps its folder.
    private static readonly EnumerationOptions AllEntries = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly ActivityMonitor? _activities;

    public EmptyFolderCleaner(ActivityMonitor? activities = null) => _activities = activities;

    /// <summary>
    /// The four managed roots under <paramref name="baseDirectory"/> (the application folder): the three work roots
    /// plus <c>data\media</c> (media assets per hardware family / DAT line / media category). Only the media root is
    /// managed — its parent <c>data\</c> (catalog, leaf databases, images, cache) is never scanned or touched.
    /// </summary>
    public static IReadOnlyList<CleanupRoot> ManagedRoots(string baseDirectory)
    {
        var b = Path.GetFullPath(baseDirectory);
        return new[]
        {
            new CleanupRoot(StagingLabel, Path.Combine(b, "staging")),
            new CleanupRoot(ArchiveLabel, Path.Combine(b, "archive")),
            new CleanupRoot(SourceLabel,  Path.Combine(b, "source")),
            new CleanupRoot(MediaLabel,   Path.Combine(b, "data", "media")),
        };
    }

    /// <summary>
    /// Why a cleanup must not start right now, or null when it may. Cleaning runs only next to the ambient
    /// <see cref="ActivityKind.Loading"/> (a view refresh); any other registered operation — or another cleanup —
    /// is a potential conflict on the same folders.
    /// </summary>
    public string? ConflictingActivity()
    {
        if (_activities is null) return null;
        var other = _activities.Active.FirstOrDefault(a => a.Kind != ActivityKind.Loading);
        return other is null ? null : $"Another operation is running ({ActivityCatalog.Label(other.Kind).TrimEnd('.')}). Wait for it to finish, then try again.";
    }

    // ── Scan ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Read-only: lists the empty directories that <see cref="Execute"/> would remove. Opens a Cleaning scope while it works.</summary>
    public EmptyFolderScan Scan(IReadOnlyList<CleanupRoot> roots, CancellationToken ct = default)
    {
        using var activity = _activities?.Begin(ActivityKind.Cleaning, "scan");
        var results = new List<EmptyFolderRootScan>();
        try
        {
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(ScanRoot(root, roots, ct));
            }
        }
        catch (OperationCanceledException)
        {
            return new EmptyFolderScan(results, cancelled: true);
        }
        return new EmptyFolderScan(results, cancelled: false);
    }

    private static EmptyFolderRootScan ScanRoot(CleanupRoot root, IReadOnlyList<CleanupRoot> all, CancellationToken ct)
    {
        var none = Array.Empty<EmptyFolderCandidate>();
        var noNotes = Array.Empty<CleanupNote>();

        var problem = ValidateRoot(root, all);
        if (problem is not null) return new EmptyFolderRootScan(root, Directory.Exists(root.Path), problem, none, noNotes);

        var info = new DirectoryInfo(root.Path);
        if (!info.Exists) return new EmptyFolderRootScan(root, rootExists: false, null, none, noNotes);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return new EmptyFolderRootScan(root, true, "The folder is a link (symlink/junction/mount point); it is not followed.", none, noNotes);

        var candidates = new List<EmptyFolderCandidate>();
        var notes = new List<CleanupNote>();
        VisitChildren(info, root, depth: 0, candidates, notes, ct);
        return new EmptyFolderRootScan(root, true, null, candidates, notes);
    }

    /// <summary>
    /// Visits the children of <paramref name="dir"/> and returns true when NOTHING in it would remain after removing
    /// the empty sub-directories found — i.e. when <paramref name="dir"/> itself is empty-or-becomes-empty.
    /// Candidates are appended in post-order (children before parents).
    /// </summary>
    private static bool VisitChildren(DirectoryInfo dir, CleanupRoot root, int depth,
        List<EmptyFolderCandidate> candidates, List<CleanupNote> notes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth >= MaxDepth)
        {
            notes.Add(new CleanupNote(dir.FullName, "Folder nesting is too deep; left untouched."));
            return false;
        }

        bool becomesEmpty = true;
        List<FileSystemInfo> children;
        try { children = dir.EnumerateFileSystemInfos("*", AllEntries).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            notes.Add(new CleanupNote(dir.FullName, "Cannot be read: " + ex.Message));
            return false;
        }

        foreach (var child in children)
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                notes.Add(new CleanupNote(child.FullName, "Link (symlink/junction/mount point): not followed, not removed."));
                becomesEmpty = false;
                continue;
            }
            if (child is not DirectoryInfo sub) { becomesEmpty = false; continue; }          // any file = content

            if ((sub.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                notes.Add(new CleanupNote(sub.FullName, "Hidden/system folder: left untouched."));
                becomesEmpty = false;
                continue;
            }

            if (VisitChildren(sub, root, depth + 1, candidates, notes, ct))
                candidates.Add(new EmptyFolderCandidate(root.Label, sub.FullName));
            else
                becomesEmpty = false;
        }
        return becomesEmpty;
    }

    private static string? ValidateRoot(CleanupRoot root, IReadOnlyList<CleanupRoot> all)
    {
        if (string.IsNullOrWhiteSpace(root.Path) || !Path.IsPathFullyQualified(root.Path))
            return "The root path is not an absolute path.";
        var full = TrimEnd(Path.GetFullPath(root.Path));
        var pathRoot = Path.GetPathRoot(full);
        if (pathRoot is not null && TrimEnd(pathRoot).Equals(full, StringComparison.OrdinalIgnoreCase))
            return "The root path is a drive root.";
        foreach (var other in all)
        {
            if (ReferenceEquals(other, root)) continue;
            var o = TrimEnd(Path.GetFullPath(other.Path));
            if (o.Equals(full, StringComparison.OrdinalIgnoreCase) ||
                IsInside(full, o) || IsInside(o, full))
                return "The root overlaps another managed root.";
        }
        return null;
    }

    // ── Execute ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the scanned candidates, deepest first. Every directory is re-validated right before its deletion and
    /// removed with a non-recursive delete only. Opens a Cleaning scope while it works (released in every outcome).
    /// </summary>
    public EmptyFolderCleanupResult Execute(EmptyFolderScan scan, CancellationToken ct = default, IProgress<EmptyFolderProgress>? progress = null)
    {
        using var activity = _activities?.Begin(ActivityKind.Cleaning, "remove");
        var removed = new List<string>();
        var skipped = new List<CleanupNote>();
        var failed  = new List<CleanupNote>();
        int total = scan.CandidateCount, done = 0;
        bool cancelled = false;

        foreach (var rootScan in scan.Roots)
        {
            if (cancelled) break;
            foreach (var candidate in rootScan.Candidates)
            {
                progress?.Report(new EmptyFolderProgress(done, total, candidate.Path));
                if (ct.IsCancellationRequested) { cancelled = true; break; }          // checked right before each deletion
                RemoveOne(rootScan.Root, candidate.Path, removed, skipped, failed);
                done++;
            }
        }
        progress?.Report(new EmptyFolderProgress(done, total, string.Empty));
        return new EmptyFolderCleanupResult(removed, skipped, failed, cancelled, total - done);
    }

    private static void RemoveOne(CleanupRoot root, string path,
        List<string> removed, List<CleanupNote> skipped, List<CleanupNote> failed)
    {
        try
        {
            var reason = Revalidate(root, path);
            if (reason is not null) { skipped.Add(new CleanupNote(path, reason)); return; }

            try
            {
                Directory.Delete(path, recursive: false);         // the OS refuses a directory with any content
                removed.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Lost a race with a writer → it is content now, not an error; anything else is a real failure.
                if (Directory.Exists(path) && HasAnyEntry(path)) skipped.Add(new CleanupNote(path, "No longer empty; left untouched."));
                else if (!Directory.Exists(path))                skipped.Add(new CleanupNote(path, "Already gone."));
                else                                              failed.Add(new CleanupNote(path, ex.Message));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            failed.Add(new CleanupNote(path, ex.Message));
        }
    }

    /// <summary>Null when <paramref name="path"/> may be deleted right now; otherwise the reason to skip it.</summary>
    private static string? Revalidate(CleanupRoot root, string path)
    {
        var rootFull = TrimEnd(Path.GetFullPath(root.Path));
        var full     = TrimEnd(Path.GetFullPath(path));

        if (!IsInside(full, rootFull)) return "Outside its managed root; not touched.";      // also excludes the root itself

        var rootInfo = new DirectoryInfo(rootFull);
        if (!rootInfo.Exists || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) return "Its root is missing or has become a link; not touched.";

        // No link anywhere between the root and the directory (a junction swapped in after the scan is never entered).
        for (var cur = new DirectoryInfo(full); cur is not null && !TrimEnd(cur.FullName).Equals(rootFull, StringComparison.OrdinalIgnoreCase); cur = cur.Parent)
            if (cur.Exists && (cur.Attributes & FileAttributes.ReparsePoint) != 0) return "Is or sits below a link; not followed.";

        var info = new DirectoryInfo(full);
        if (!info.Exists) return "Already gone.";
        if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return "Hidden/system folder: left untouched.";
        if (HasAnyEntry(full)) return "No longer empty; left untouched.";
        return null;
    }

    private static bool HasAnyEntry(string path)
        => new DirectoryInfo(path).EnumerateFileSystemInfos("*", AllEntries).Any();

    // ── Path helpers ────────────────────────────────────────────────────────────────────────────────────────────

    private static string TrimEnd(string p) => p.Length > 3 ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p;

    /// <summary>True when <paramref name="child"/> is strictly below <paramref name="parent"/> (not equal).</summary>
    private static bool IsInside(string child, string parent)
        => child.Length > parent.Length
           && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
           && (child[parent.Length] == Path.DirectorySeparatorChar || child[parent.Length] == Path.AltDirectorySeparatorChar);
}
