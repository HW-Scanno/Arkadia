using System;
using System.IO;
using System.Linq;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Conservative cleanup of leftover cue-only staging folders whose release is already
/// satisfied by a durable copy. Files are moved to incoming-skip (never deleted); folders
/// with a <c>.bin</c> or an unsatisfied release are left untouched.
/// </summary>
public sealed class StaleCueOnlyStagingCleanupTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _staging;
    private readonly string _skip;

    public StaleCueOnlyStagingCleanupTests()
    {
        _tmp     = Path.Combine(Path.GetTempPath(), "ArkCueClean_" + Guid.NewGuid().ToString("N")[..8]);
        _staging = Path.Combine(_tmp, "staging", "ps2", "redump");
        _skip    = Path.Combine(_tmp, "incoming-skip", "ps2");
        Directory.CreateDirectory(_staging);
    }

    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    private string MakeFolder(string safeFolder, params string[] fileNames)
    {
        var dir = Path.Combine(_staging, safeFolder);
        Directory.CreateDirectory(dir);
        foreach (var f in fileNames) File.WriteAllText(Path.Combine(dir, f), "x");
        return dir;
    }

    // ── pure decision ─────────────────────────────────────────────────────────

    [Fact]
    public void IsCueOnly_TrueForOnlyCueFiles()
        => Assert.True(StaleCueOnlyStagingCleanup.IsCueOnly(new[] { "Game.cue", "Game (Disc 2).cue" }));

    [Fact]
    public void IsCueOnly_FalseWhenBinPresent()
        => Assert.False(StaleCueOnlyStagingCleanup.IsCueOnly(new[] { "Game.cue", "Game.bin" }));

    [Fact]
    public void IsCueOnly_FalseForEmpty()
        => Assert.False(StaleCueOnlyStagingCleanup.IsCueOnly(Array.Empty<string>()));

    // ── Run() with real temp filesystem ────────────────────────────────────────

    [Fact]
    public void StaleCueOnlyStagingForSatisfiedRelease_IsMovedToIncomingSkip()
    {
        var dir = MakeFolder("Game A", "Game A.cue");

        var result = StaleCueOnlyStagingCleanup.Run(_staging, _skip, _ => true);   // satisfied

        Assert.Equal(1, result.Moved);
        Assert.False(Directory.Exists(dir));                                        // empty folder removed
        Assert.True(File.Exists(Path.Combine(_skip, "Game A.cue")));                // moved, not deleted
        Assert.Contains(result.Operations, o => o.Action == "stale-cue-only-staging-moved");
    }

    [Fact]
    public void StaleCueOnlyStagingWithBin_IsNotCleaned()
    {
        var dir = MakeFolder("Game B", "Game B.cue", "Game B.bin");

        var result = StaleCueOnlyStagingCleanup.Run(_staging, _skip, _ => true);   // even if "satisfied"

        Assert.Equal(0, result.Moved);
        Assert.True(Directory.Exists(dir));                                         // untouched
        Assert.True(File.Exists(Path.Combine(dir, "Game B.bin")));
    }

    [Fact]
    public void StaleCueOnlyStagingForUnsatisfiedRelease_IsNotCleaned()
    {
        var dir = MakeFolder("Game C", "Game C.cue");

        var result = StaleCueOnlyStagingCleanup.Run(_staging, _skip, _ => false);  // not satisfied

        Assert.Equal(0, result.Moved);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Run_OnlyCleansSatisfiedCueOnlyFolders_Mixed()
    {
        MakeFolder("Sat Cue",   "Sat.cue");                 // satisfied + cue-only → cleaned
        MakeFolder("Sat Bin",   "S.cue", "S.bin");          // satisfied + has bin → kept
        var unsatDir = MakeFolder("Unsat Cue", "U.cue");    // cue-only but unsatisfied → kept

        // "Sat*" folders are satisfied; "Unsat*" is not.
        var result = StaleCueOnlyStagingCleanup.Run(_staging, _skip,
            folder => folder.StartsWith("Sat", StringComparison.Ordinal));

        Assert.Equal(1, result.Moved);                                              // only "Sat Cue"
        Assert.False(Directory.Exists(Path.Combine(_staging, "Sat Cue")));
        Assert.True(Directory.Exists(Path.Combine(_staging, "Sat Bin")));
        Assert.True(Directory.Exists(unsatDir));
    }

    [Fact]
    public void Run_NoStagingRoot_IsNoOp()
    {
        var result = StaleCueOnlyStagingCleanup.Run(
            Path.Combine(_tmp, "does-not-exist"), _skip, _ => true);
        Assert.Equal(0, result.Moved);
    }
}
