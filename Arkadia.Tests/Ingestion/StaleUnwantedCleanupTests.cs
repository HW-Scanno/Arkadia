using System;
using System.IO;
using System.Linq;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Filesystem tests for the real <see cref="StaleUnwantedCleanup"/> helper — the
/// conservative relocation of stale staging/source files for now-unwanted
/// releases. These exercise production code against a real temp directory tree.
/// </summary>
public sealed class StaleUnwantedCleanupTests : IDisposable
{
    private readonly string _root;
    private readonly string _stagingRoot;
    private readonly string _sourceRoot;
    private readonly string _skipDir;

    public StaleUnwantedCleanupTests()
    {
        _root        = Path.Combine(Path.GetTempPath(), "ArkStale_" + Guid.NewGuid().ToString("N")[..8]);
        _stagingRoot = Path.Combine(_root, "staging", "ps2", "ps2-redump");
        _sourceRoot  = Path.Combine(_root, "source",  "ps2", "ps2-redump");
        _skipDir     = Path.Combine(_root, "incoming-skip", "ps2");
        Directory.CreateDirectory(_stagingRoot);
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_skipDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string StageFile(string root, string releaseName, string fileName, byte[]? content = null)
    {
        var folder = Path.Combine(root, IngestionPaths.SafeFolderName(releaseName));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, content ?? new byte[] { 1, 2, 3 });
        return path;
    }

    private StaleUnwantedCleanupResult Run(params (string Name, string Status)[] releases) =>
        StaleUnwantedCleanup.Run(_stagingRoot, _sourceRoot, _skipDir, releases.ToList());

    // ── 1 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_UnwantedRelease_StaleStagingMovedToIncomingSkip()
    {
        var staged = StageFile(_stagingRoot, "Unwanted Disc", "disc.bin");

        var result = Run(("Unwanted Disc", "unwanted"));

        Assert.False(File.Exists(staged), "stale staging file must be moved out");
        Assert.True(File.Exists(Path.Combine(_skipDir, "disc.bin")), "file must land in incoming-skip");
        Assert.Equal(1, result.StaleStagingMoved);
        Assert.Equal(0, result.StaleSourceMoved);
    }

    // ── 2 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_UnwantedRelease_StaleSourceMovedToIncomingSkip()
    {
        var srcFile = StageFile(_sourceRoot, "Vetoed Game", "game.iso");

        var result = Run(("Vetoed Game", "unwanted"));

        Assert.False(File.Exists(srcFile));
        Assert.True(File.Exists(Path.Combine(_skipDir, "game.iso")));
        Assert.Equal(1, result.StaleSourceMoved);
        Assert.Equal(0, result.StaleStagingMoved);
    }

    // ── 3 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_UnwantedStaleCleanup_UsesCollisionSafeNames()
    {
        // A file with the same name already sits in incoming-skip.
        File.WriteAllBytes(Path.Combine(_skipDir, "disc.bin"), new byte[] { 9, 9 });
        StageFile(_stagingRoot, "Unwanted Disc", "disc.bin");

        var result = Run(("Unwanted Disc", "unwanted"));

        Assert.True(File.Exists(Path.Combine(_skipDir, "disc.bin")),      "original must be preserved");
        Assert.True(File.Exists(Path.Combine(_skipDir, "disc (1).bin")),  "moved file must get a collision-safe name");
        Assert.Equal(1, result.StaleStagingMoved);
    }

    // ── 4 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_UnwantedStaleCleanup_RemovesEmptyReleaseFolder()
    {
        StageFile(_stagingRoot, "Unwanted Disc", "disc.bin");
        var folder = Path.Combine(_stagingRoot, IngestionPaths.SafeFolderName("Unwanted Disc"));

        Run(("Unwanted Disc", "unwanted"));

        Assert.False(Directory.Exists(folder), "emptied release folder must be removed");
    }

    // ── 5 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_UnwantedStaleCleanup_DoesNotTouchWantedRelease()
    {
        var staged = StageFile(_stagingRoot, "Mario", "mario.iso");
        var srcd   = StageFile(_sourceRoot,  "Mario", "mario.iso");

        var result = Run(("Mario", "present"));

        Assert.True(File.Exists(staged), "wanted staging must be left untouched");
        Assert.True(File.Exists(srcd),   "wanted source must be left untouched");
        Assert.Equal(0, result.StaleStagingMoved);
        Assert.Equal(0, result.StaleSourceMoved);
        Assert.Empty(result.Operations);
    }

    // ── 6 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_UnwantedStaleCleanup_DoesNotTouchAmbiguousFolder()
    {
        // Two releases whose names sanitize to the SAME folder ("Zelda"): one
        // unwanted, one present. The folder is ambiguous → must be left alone.
        Assert.Equal(
            IngestionPaths.SafeFolderName("Zelda"),
            IngestionPaths.SafeFolderName("  Zelda  "));

        var staged = StageFile(_stagingRoot, "Zelda", "zelda.iso");

        var result = Run(("Zelda", "unwanted"), ("  Zelda  ", "present"));

        Assert.True(File.Exists(staged), "ambiguous (collision with a wanted release) folder must be skipped");
        Assert.Equal(0, result.StaleStagingMoved);
    }

    // ── 6b — orphan folder (no matching release) is skipped ──────────────────

    [Fact]
    public void Ingestion_UnwantedStaleCleanup_DoesNotTouchOrphanFolder()
    {
        var staged = StageFile(_stagingRoot, "Ghost Release", "ghost.iso");

        // No release named "Ghost Release" exists in the DB set.
        var result = Run(("Some Other Unwanted", "unwanted"));

        Assert.True(File.Exists(staged), "orphan folder with no matching release must be skipped");
        Assert.Equal(0, result.StaleStagingMoved);
    }

    // ── 7 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_UnwantedStaleCleanup_DoesNotDeleteOnMoveFailure()
    {
        var staged = StageFile(_stagingRoot, "Locked Disc", "locked.bin");
        var folder = Path.Combine(_stagingRoot, IngestionPaths.SafeFolderName("Locked Disc"));

        // Hold an exclusive handle so File.Move cannot rename the file.
        using (var _ = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = Run(("Locked Disc", "unwanted"));

            Assert.True(File.Exists(staged), "file must NOT be deleted when the move fails");
            Assert.True(Directory.Exists(folder), "folder must be retained when a move failed");
            Assert.Equal(0, result.StaleStagingMoved);
            Assert.Contains(result.Operations, o => o.Action == "stale-staging-cleanup-failed");
        }
    }

    // ── 8 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_UnwantedStaleCleanup_LogsMovedFiles()
    {
        StageFile(_stagingRoot, "Unwanted Disc", "disc.bin");
        StageFile(_sourceRoot,  "Unwanted Disc", "disc.iso");

        var result = Run(("Unwanted Disc", "unwanted"));

        Assert.Contains(result.Operations,
            o => o.Action == "stale-staging-unwanted-moved" && o.Object == "disc.bin"
                 && o.Destination.StartsWith("incoming-skip/ps2/"));
        Assert.Contains(result.Operations,
            o => o.Action == "stale-source-unwanted-moved" && o.Object == "disc.iso");
    }

    // ── 9 — interrupted wanted staging is explicitly OUT OF SCOPE ────────────

    [Fact]
    public void Ingestion_InterruptedWantedStaging_RemainsOutOfScopeOrReported()
    {
        // A wanted release with complete staging but no derived artifact (interrupted
        // run) is NOT cleaned by this helper — it only acts on unwanted releases.
        var a = StageFile(_stagingRoot, "Interrupted", "part1.bin");
        var b = StageFile(_stagingRoot, "Interrupted", "part2.bin");

        var result = Run(("Interrupted", "missing"));

        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Equal(0, result.StaleStagingMoved);
        Assert.Empty(result.Operations);
    }
}
