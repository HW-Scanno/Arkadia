using System;
using System.IO;
using System.Linq;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// <see cref="IngestionPaths.RemoveEmptyDirectoriesBottomUp"/>: collapses empty nested branches deepest-first,
/// never deletes a file, and preserves any branch (and its ancestors) that still holds material.
/// </summary>
public sealed class EmptyDirectoryCleanupTests : IDisposable
{
    private readonly string _dir;

    public EmptyDirectoryCleanupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkEmptyDir_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Nested(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

    // ── Deep empty hierarchy: a/b/c/d fully removed, and the root itself ──────

    [Fact]
    public void DeepEmptyHierarchy_FullyRemoved()
    {
        var release = Nested("release");
        Directory.CreateDirectory(Path.Combine(release, "a", "b", "c", "d"));

        IngestionPaths.RemoveEmptyDirectoriesBottomUp(release);

        Assert.False(Directory.Exists(release));   // drained to nothing, root removed
    }

    // ── Partial: one leftover file preserved with all ancestors; empty siblings removed ──

    [Fact]
    public void LeftoverFile_PreservedWithAncestors_EmptySiblingsRemoved()
    {
        var release = Nested("release");
        // Surviving branch holds a file…
        Directory.CreateDirectory(Path.Combine(release, "TIME-PILOT", "assets"));
        File.WriteAllBytes(Path.Combine(release, "TIME-PILOT", "assets", "unresolved.bin"), new byte[] { 1 });
        // …and two genuinely-empty sibling branches.
        Directory.CreateDirectory(Path.Combine(release, "LAUNCHER.DATA", ".META"));
        Directory.CreateDirectory(Path.Combine(release, "EMPTY", "deep", "deeper"));

        IngestionPaths.RemoveEmptyDirectoriesBottomUp(release);

        // File and its whole ancestor chain preserved — no file deletion.
        Assert.True(File.Exists(Path.Combine(release, "TIME-PILOT", "assets", "unresolved.bin")));
        Assert.True(Directory.Exists(Path.Combine(release, "TIME-PILOT", "assets")));
        Assert.True(Directory.Exists(release));                                       // root kept (still holds a file)
        // Empty sibling branches gone.
        Assert.False(Directory.Exists(Path.Combine(release, "LAUNCHER.DATA")));
        Assert.False(Directory.Exists(Path.Combine(release, "EMPTY")));
    }

    // ── Flat: an empty release dir with no subdirs is removed (unchanged behavior) ──

    [Fact]
    public void FlatEmptyRelease_Removed()
    {
        var release = Nested("Game B");
        Directory.CreateDirectory(release);   // flat, already drained of files

        IngestionPaths.RemoveEmptyDirectoriesBottomUp(release);

        Assert.False(Directory.Exists(release));
    }

    // ── Safety: a directly-held file keeps the release dir; nested empties still collapse ──

    [Fact]
    public void FileAtRoot_KeepsRoot_NestedEmptiesCollapse()
    {
        var release = Nested("release");
        Directory.CreateDirectory(Path.Combine(release, "empty", "child"));
        File.WriteAllBytes(Path.Combine(release, "keep.bin"), new byte[] { 9 });

        IngestionPaths.RemoveEmptyDirectoriesBottomUp(release);

        Assert.True(File.Exists(Path.Combine(release, "keep.bin")));
        Assert.True(Directory.Exists(release));
        Assert.False(Directory.Exists(Path.Combine(release, "empty")));
    }

    // ── removeRoot:false keeps the root even when empty (collapses only its children) ──

    [Fact]
    public void RemoveRootFalse_KeepsEmptyRoot()
    {
        var release = Nested("release");
        Directory.CreateDirectory(Path.Combine(release, "a", "b"));

        IngestionPaths.RemoveEmptyDirectoriesBottomUp(release, removeRoot: false);

        Assert.True(Directory.Exists(release));                       // root kept
        Assert.False(Directory.Exists(Path.Combine(release, "a")));   // empty children removed
    }

    [Fact]
    public void MissingRoot_NoThrow()
        => IngestionPaths.RemoveEmptyDirectoriesBottomUp(Nested("does-not-exist"));   // must not throw
}
