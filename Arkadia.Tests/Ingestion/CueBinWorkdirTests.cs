using System;
using System.IO;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

public sealed class CueBinWorkdirTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "ArkTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var d in dirs)
            if (Directory.Exists(d))
                try { Directory.Delete(d, recursive: true); } catch { }
    }

    // ── RewriteCueContent — pure function tests ───────────────────────────────

    [Fact]
    public void RewritesSingleFileLineToTrack01Bin()
    {
        const string content = "FILE \"long game name.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n";
        var result = CueBinWorkdir.RewriteCueContent(content, ["long game name.bin"], out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Contains("\"track01.bin\"", result);
        Assert.DoesNotContain("long game name", result);
    }

    [Fact]
    public void RewritesMultipleFileLinesToTrackNumbers()
    {
        const string content =
            "FILE \"disc (Track 1).bin\" BINARY\r\n" +
            "  TRACK 01 MODE2/2352\r\n" +
            "FILE \"disc (Track 2).bin\" BINARY\r\n" +
            "  TRACK 02 MODE2/2352\r\n";
        var result = CueBinWorkdir.RewriteCueContent(
            content, ["disc (Track 1).bin", "disc (Track 2).bin"], out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Contains("\"track01.bin\"", result);
        Assert.Contains("\"track02.bin\"", result);
        Assert.DoesNotContain("disc (Track", result);
    }

    [Fact]
    public void PreservesNonFileLines()
    {
        const string content =
            "  TRACK 01 MODE2/2352\r\n" +
            "    INDEX 01 00:00:00\r\n" +
            "    INDEX 00 00:01:00\r\n";
        var result = CueBinWorkdir.RewriteCueContent(content, [], out var error);
        Assert.Null(error);
        Assert.Equal(content, result);
    }

    [Fact]
    public void FailsClearlyWhenCueReferencesUnknownBin()
    {
        const string content = "FILE \"mystery.bin\" BINARY\r\n";
        var result = CueBinWorkdir.RewriteCueContent(content, ["known.bin"], out var error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("mystery.bin", error);
        Assert.Contains("known.bin",   error);
    }

    [Fact]
    public void FileLineMatchingIsCaseInsensitive()
    {
        const string content = "FILE \"Game.BIN\" BINARY\r\n";
        // knownBinNames uses different case
        var result = CueBinWorkdir.RewriteCueContent(content, ["game.bin"], out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Contains("\"track01.bin\"", result);
    }

    [Fact]
    public void SameFilePresentTwice_AssignedSameShortName()
    {
        // Unusual but legal: same FILE referenced twice in the CUE.
        const string content =
            "FILE \"game.bin\" BINARY\r\n" +
            "FILE \"game.bin\" BINARY\r\n";
        var result = CueBinWorkdir.RewriteCueContent(content, ["game.bin"], out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        // Both occurrences should map to the same short name.
        Assert.Equal(2, result!.Split(["\"track01.bin\""], StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("track02", result);
    }

    [Fact]
    public void PreservesLineEndings_CRLF()
    {
        const string content = "FILE \"a.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n";
        var result = CueBinWorkdir.RewriteCueContent(content, ["a.bin"], out _);
        Assert.NotNull(result);
        Assert.Contains("\r\n", result);
    }

    [Fact]
    public void PreservesLineEndings_LF()
    {
        const string content = "FILE \"a.bin\" BINARY\n  TRACK 01 MODE2/2352\n";
        var result = CueBinWorkdir.RewriteCueContent(content, ["a.bin"], out _);
        Assert.NotNull(result);
        // Should not have gained \r characters.
        Assert.DoesNotContain("\r\n", result);
    }

    // ── PrepareWorkdir — filesystem tests (no chdman) ────────────────────────

    [Fact]
    public void PrepareWorkdir_CreatesInputCueWithShortBinReference()
    {
        var srcDir  = TempDir();
        var appRoot = TempDir();
        try
        {
            const string cueName = "game.cue";
            const string binName = "game.bin";
            File.WriteAllText(Path.Combine(srcDir, cueName),
                $"FILE \"{binName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n");
            File.WriteAllBytes(Path.Combine(srcDir, binName), [0x01, 0x02, 0x03]);

            var (ok, workdir, error) = CueBinWorkdir.PrepareWorkdir(
                appRoot, srcDir, cueName, [binName]);
            try
            {
                Assert.True(ok, $"PrepareWorkdir failed: {error}");
                var inputCue = Path.Combine(workdir, "input.cue");
                Assert.True(File.Exists(inputCue));
                var cueText = File.ReadAllText(inputCue);
                Assert.Contains("\"track01.bin\"", cueText);
                Assert.DoesNotContain(binName, cueText);
            }
            finally { Cleanup(workdir); }
        }
        finally { Cleanup(srcDir, appRoot); }
    }

    [Fact]
    public void PrepareWorkdir_CopiesBinWithShortName()
    {
        var srcDir  = TempDir();
        var appRoot = TempDir();
        try
        {
            const string cueName = "game.cue";
            const string binName = "game.bin";
            var binBytes = new byte[] { 0xAA, 0xBB, 0xCC };
            File.WriteAllText(Path.Combine(srcDir, cueName),
                $"FILE \"{binName}\" BINARY\r\n");
            File.WriteAllBytes(Path.Combine(srcDir, binName), binBytes);

            var (ok, workdir, _) = CueBinWorkdir.PrepareWorkdir(
                appRoot, srcDir, cueName, [binName]);
            try
            {
                Assert.True(ok);
                var track01 = Path.Combine(workdir, "track01.bin");
                Assert.True(File.Exists(track01), "track01.bin should exist in workdir");
                Assert.Equal(binBytes, File.ReadAllBytes(track01));
            }
            finally { Cleanup(workdir); }
        }
        finally { Cleanup(srcDir, appRoot); }
    }

    [Fact]
    public void PrepareWorkdir_DoesNotModifyOriginalCue()
    {
        var srcDir  = TempDir();
        var appRoot = TempDir();
        try
        {
            const string cueName    = "original.cue";
            const string binName    = "original.bin";
            const string cueContent = "FILE \"original.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n";
            File.WriteAllText(Path.Combine(srcDir, cueName), cueContent);
            File.WriteAllBytes(Path.Combine(srcDir, binName), [0x00]);

            var (_, workdir, _) = CueBinWorkdir.PrepareWorkdir(
                appRoot, srcDir, cueName, [binName]);
            try
            {
                Assert.Equal(cueContent, File.ReadAllText(Path.Combine(srcDir, cueName)));
            }
            finally { Cleanup(workdir); }
        }
        finally { Cleanup(srcDir, appRoot); }
    }

    [Fact]
    public void PrepareWorkdir_UsesShortPathsForCueAndBin()
    {
        var srcDir  = TempDir();
        var appRoot = TempDir();
        try
        {
            const string cueName = "3 Title Special Disc - Saru! Get You! 2 & PoPoLoCrois - Hajimari no Bouken.cue";
            const string binName = "3 Title Special Disc - Saru! Get You! 2 & PoPoLoCrois - Hajimari no Bouken.bin";
            File.WriteAllText(Path.Combine(srcDir, cueName),
                $"FILE \"{binName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n");
            File.WriteAllBytes(Path.Combine(srcDir, binName), [0x00]);

            var (ok, workdir, error) = CueBinWorkdir.PrepareWorkdir(
                appRoot, srcDir, cueName, [binName]);
            try
            {
                Assert.True(ok, $"PrepareWorkdir failed: {error}");
                // Workdir itself should be short
                Assert.True(workdir.Length < 200, $"Workdir path too long: {workdir}");
                // input.cue in workdir must NOT contain the long bin name
                var inputCuePath = Path.Combine(workdir, "input.cue");
                Assert.True(File.Exists(inputCuePath));
                var cueText = File.ReadAllText(inputCuePath);
                Assert.DoesNotContain("PoPoLoCrois", cueText);
                Assert.Contains("\"track01.bin\"", cueText);
                // track01.bin must exist in the workdir
                Assert.True(File.Exists(Path.Combine(workdir, "track01.bin")));
            }
            finally { Cleanup(workdir); }
        }
        finally { Cleanup(srcDir, appRoot); }
    }

    [Fact]
    public void PrepareWorkdir_MultipleTracks_AllCopiedWithCorrectShortNames()
    {
        var srcDir  = TempDir();
        var appRoot = TempDir();
        try
        {
            const string cueName = "game.cue";
            const string bin1    = "game (Track 1).bin";
            const string bin2    = "game (Track 2).bin";
            const string bin3    = "game (Track 3).bin";
            var cueContent =
                $"FILE \"{bin1}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n" +
                $"FILE \"{bin2}\" BINARY\r\n  TRACK 02 MODE2/2352\r\n" +
                $"FILE \"{bin3}\" BINARY\r\n  TRACK 03 MODE2/2352\r\n";
            File.WriteAllText(Path.Combine(srcDir, cueName), cueContent);
            File.WriteAllBytes(Path.Combine(srcDir, bin1), [0x01]);
            File.WriteAllBytes(Path.Combine(srcDir, bin2), [0x02]);
            File.WriteAllBytes(Path.Combine(srcDir, bin3), [0x03]);

            var (ok, workdir, _) = CueBinWorkdir.PrepareWorkdir(
                appRoot, srcDir, cueName, [bin1, bin2, bin3]);
            try
            {
                Assert.True(ok);
                Assert.True(File.Exists(Path.Combine(workdir, "track01.bin")));
                Assert.True(File.Exists(Path.Combine(workdir, "track02.bin")));
                Assert.True(File.Exists(Path.Combine(workdir, "track03.bin")));
                // Verify content matches original order
                Assert.Equal(new byte[] { 0x01 }, File.ReadAllBytes(Path.Combine(workdir, "track01.bin")));
                Assert.Equal(new byte[] { 0x02 }, File.ReadAllBytes(Path.Combine(workdir, "track02.bin")));
                Assert.Equal(new byte[] { 0x03 }, File.ReadAllBytes(Path.Combine(workdir, "track03.bin")));
            }
            finally { Cleanup(workdir); }
        }
        finally { Cleanup(srcDir, appRoot); }
    }

    [Fact]
    public void PrepareWorkdir_FailsWithClearError_WhenCueReferencesUnknownBin()
    {
        var srcDir  = TempDir();
        var appRoot = TempDir();
        try
        {
            const string cueName = "game.cue";
            File.WriteAllText(Path.Combine(srcDir, cueName),
                "FILE \"unlisted.bin\" BINARY\r\n");

            var (ok, workdir, error) = CueBinWorkdir.PrepareWorkdir(
                appRoot, srcDir, cueName, ["game.bin"]);
            Cleanup(workdir);
            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Contains("unlisted.bin", error);
        }
        finally { Cleanup(srcDir, appRoot); }
    }

    // ── Hardlink behaviour ────────────────────────────────────────────────────

    [Fact]
    public void CueBinWorkdir_UsesHardlink_WhenAvailable()
    {
        var srcDir  = TempDir();
        var appRoot = TempDir();
        try
        {
            const string cueName = "game.cue";
            const string binName = "game.bin";
            File.WriteAllText(Path.Combine(srcDir, cueName),
                $"FILE \"{binName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n");
            File.WriteAllBytes(Path.Combine(srcDir, binName), [0x01, 0x02]);

            int hardlinkAttempts = 0;
            bool Spy(string dest, string src)
            {
                hardlinkAttempts++;
                return CueBinWorkdir.TryHardLink(dest, src);
            }

            var (ok, workdir, error) = CueBinWorkdir.PrepareWorkdir(
                appRoot, srcDir, cueName, [binName], Spy);
            try
            {
                Assert.True(ok, $"PrepareWorkdir failed: {error}");
                Assert.Equal(1, hardlinkAttempts);
                Assert.True(File.Exists(Path.Combine(workdir, "track01.bin")));
            }
            finally { Cleanup(workdir); }
        }
        finally { Cleanup(srcDir, appRoot); }
    }

    [Fact]
    public void CueBinWorkdir_FallsBackToCopy_WhenHardlinkFails()
    {
        var srcDir  = TempDir();
        var appRoot = TempDir();
        try
        {
            const string cueName = "game.cue";
            const string binName = "game.bin";
            var binData = new byte[] { 0xAA, 0xBB, 0xCC };
            File.WriteAllText(Path.Combine(srcDir, cueName),
                $"FILE \"{binName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n");
            File.WriteAllBytes(Path.Combine(srcDir, binName), binData);

            var (ok, workdir, error) = CueBinWorkdir.PrepareWorkdir(
                appRoot, srcDir, cueName, [binName], hardlinkAttempt: (_, _) => false);
            try
            {
                Assert.True(ok, $"PrepareWorkdir failed: {error}");
                var track = Path.Combine(workdir, "track01.bin");
                Assert.True(File.Exists(track), "track01.bin must exist via copy fallback");
                Assert.Equal(binData, File.ReadAllBytes(track));
            }
            finally { Cleanup(workdir); }
        }
        finally { Cleanup(srcDir, appRoot); }
    }

    [Fact]
    public void CueBinWorkdir_HardlinkCleanup_DoesNotDeleteSource()
    {
        var srcDir  = TempDir();
        var appRoot = TempDir();
        try
        {
            const string cueName = "game.cue";
            const string binName = "game.bin";
            var binData = new byte[] { 0x11, 0x22, 0x33, 0x44 };
            File.WriteAllText(Path.Combine(srcDir, cueName),
                $"FILE \"{binName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n");
            File.WriteAllBytes(Path.Combine(srcDir, binName), binData);

            var (ok, workdir, _) = CueBinWorkdir.PrepareWorkdir(
                appRoot, srcDir, cueName, [binName]);
            Assert.True(ok);

            // Simulate workdir cleanup that happens after a successful transform.
            Directory.Delete(workdir, recursive: true);

            // Whether hardlink or copy was used, the source BIN must survive cleanup.
            var srcBinPath = Path.Combine(srcDir, binName);
            Assert.True(File.Exists(srcBinPath), "Source BIN must survive workdir deletion");
            Assert.Equal(binData, File.ReadAllBytes(srcBinPath));
        }
        finally { Cleanup(srcDir, appRoot); }
    }
}
