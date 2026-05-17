using System;
using System.IO;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

public sealed class StagingMoveTests : IDisposable
{
    private readonly string _tempRoot;

    public StagingMoveTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(), "ArkStagingTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private string MakeDir(string name)
    {
        var d = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // ── StageSingleTargetSameVolume_UsesMove ──────────────────────────────────

    [Fact]
    public void StageSingleTargetSameVolume_UsesMove()
    {
        var src  = Path.Combine(MakeDir("incoming"), "game.bin");
        var dest = Path.Combine(MakeDir("staging"),  "game.bin");
        File.WriteAllBytes(src, [0x01, 0x02, 0x03]);

        StagingHelpers.StageFile(src, dest, pendingCount: 1, out var opName);

        Assert.Equal("stage-moved", opName);
        Assert.True(File.Exists(dest),  "Destination must exist after move");
        Assert.False(File.Exists(src),  "Source must be absent after move");
    }

    // ── StageMultiTarget_UsesCopy ─────────────────────────────────────────────

    [Fact]
    public void StageMultiTarget_UsesCopy()
    {
        var src  = Path.Combine(MakeDir("incoming2"), "game.bin");
        var dest = Path.Combine(MakeDir("staging2"),  "game.bin");
        File.WriteAllBytes(src, [0x01, 0x02, 0x03]);

        StagingHelpers.StageFile(src, dest, pendingCount: 2, out var opName);

        Assert.Equal("copy", opName);
        Assert.True(File.Exists(dest), "Destination must exist after copy");
        Assert.True(File.Exists(src),  "Source must still exist after copy");
    }

    // ── StageMoveLeavesFileInStaging ──────────────────────────────────────────

    [Fact]
    public void StageMoveLeavesFileInStaging()
    {
        var content = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var src  = Path.Combine(MakeDir("incoming3"), "data.bin");
        var dest = Path.Combine(MakeDir("staging3"),  "data.bin");
        File.WriteAllBytes(src, content);

        StagingHelpers.StageFile(src, dest, pendingCount: 1, out _);

        Assert.True(File.Exists(dest), "File must be present in staging after move");
        Assert.Equal(content, File.ReadAllBytes(dest));
    }

    // ── StageMoveDoesNotRunForMultiTargetFanout ───────────────────────────────

    [Fact]
    public void StageMoveDoesNotRunForMultiTargetFanout()
    {
        var src  = Path.Combine(MakeDir("incoming4"), "shared.bin");
        var dest = Path.Combine(MakeDir("staging4"),  "shared.bin");
        File.WriteAllBytes(src, [0xFF]);

        // pendingCount > 1 forces copy even on the same volume
        StagingHelpers.StageFile(src, dest, pendingCount: 3, out var opName);

        Assert.Equal("copy", opName);
        Assert.True(File.Exists(src),  "Source must survive when pendingCount > 1");
        Assert.True(File.Exists(dest), "Destination must exist after copy");
    }
}
