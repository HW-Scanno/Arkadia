using System;
using System.IO;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

public sealed class IsoChdWorkdirTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TempDir()
    {
        var d = Path.Combine(
            Path.GetTempPath(), "ArkIsoTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var p in paths)
            if (Directory.Exists(p))
                try { Directory.Delete(p, recursive: true); } catch { }
    }

    // Fake transform: creates output.chd with the given content and reports success.
    private static Func<string, string, (bool, string)> FakeSuccess(byte[] outputContent) =>
        (_, outp) => { File.WriteAllBytes(outp, outputContent); return (true, ""); };

    // Fake transform: reports failure without creating any output.
    private static Func<string, string, (bool, string)> FakeFailure(string message = "simulated failure") =>
        (_, _) => (false, message);

    // Minimal records — values unused when executeTransformOverride is provided.
    private static TransformRecord FakeXform() =>
        new() { Id = "test-xform", Name = "Test", CommandTemplate = "" };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IsoChdWorkdir_PreparesInputIsoInShortWorkdir()
    {
        var appRoot = TempDir();
        var isoDir  = TempDir();
        try
        {
            var isoPath   = Path.Combine(isoDir, "game.iso");
            var finalPath = Path.Combine(appRoot, "out.chd");
            File.WriteAllBytes(isoPath, [0x01, 0x02]);

            string? capturedInput = null;
            (bool, string) CapturingTransform(string inp, string outp)
            {
                capturedInput = inp;
                File.WriteAllBytes(outp, [0xAA]);
                return (true, "");
            }

            bool ok = IsoChdWorkdir.Run(
                appRoot, FakeXform(), null, isoPath, finalPath,
                out _, out _, out _,
                executeTransformOverride: CapturingTransform);

            Assert.True(ok);
            Assert.NotNull(capturedInput);
            // chdman must receive the workdir-local input.iso, not the original long path
            Assert.Equal("input.iso", Path.GetFileName(capturedInput));
            // the workdir path itself should be short (under transform-work/chd/<8char>)
            Assert.True(Path.GetDirectoryName(capturedInput)!.Length < 260,
                $"Input path should be short but was: {capturedInput}");
        }
        finally { Cleanup(appRoot, isoDir); }
    }

    [Fact]
    public void IsoChdWorkdir_UsesHardlinkOrMaterializer()
    {
        var appRoot = TempDir();
        var isoDir  = TempDir();
        try
        {
            var isoPath   = Path.Combine(isoDir, "game.iso");
            var finalPath = Path.Combine(appRoot, "out.chd");
            File.WriteAllBytes(isoPath, [0x01, 0x02]);

            int hardlinkAttempts = 0;
            bool HardlinkSpy(string dest, string src)
            {
                hardlinkAttempts++;
                return CueBinWorkdir.TryHardLink(dest, src);
            }

            bool ok = IsoChdWorkdir.Run(
                appRoot, FakeXform(), null, isoPath, finalPath,
                out _, out _, out _,
                hardlinkAttempt: HardlinkSpy,
                executeTransformOverride: FakeSuccess([0xBB]));

            Assert.True(ok);
            Assert.Equal(1, hardlinkAttempts);
        }
        finally { Cleanup(appRoot, isoDir); }
    }

    [Fact]
    public void IsoChdWorkdir_MovesOutputToFinalArchiveAfterSuccess()
    {
        var appRoot = TempDir();
        var isoDir  = TempDir();
        var archive = TempDir();
        try
        {
            var isoPath    = Path.Combine(isoDir, "game.iso");
            var finalPath  = Path.Combine(archive, "game.chd");
            var chdContent = new byte[] { 0x11, 0x22, 0x33 };
            File.WriteAllBytes(isoPath, [0x01]);

            bool ok = IsoChdWorkdir.Run(
                appRoot, FakeXform(), null, isoPath, finalPath,
                out _, out _, out _,
                executeTransformOverride: FakeSuccess(chdContent));

            Assert.True(ok);
            Assert.True(File.Exists(finalPath), "Final .chd must exist after success");
            Assert.Equal(chdContent, File.ReadAllBytes(finalPath));
        }
        finally { Cleanup(appRoot, isoDir, archive); }
    }

    [Fact]
    public void IsoChdWorkdir_DoesNotCreateFinalArchiveOnFailure()
    {
        var appRoot = TempDir();
        var isoDir  = TempDir();
        var archive = TempDir();
        try
        {
            var isoPath   = Path.Combine(isoDir, "game.iso");
            var finalPath = Path.Combine(archive, "game.chd");
            File.WriteAllBytes(isoPath, [0x01]);

            bool ok = IsoChdWorkdir.Run(
                appRoot, FakeXform(), null, isoPath, finalPath,
                out _, out _, out var err,
                executeTransformOverride: FakeFailure());

            Assert.False(ok);
            Assert.False(File.Exists(finalPath),
                "archive/.chd must NOT be created when transform fails");
            Assert.NotEmpty(err);
        }
        finally { Cleanup(appRoot, isoDir, archive); }
    }

    [Fact]
    public void IsoChdWorkdir_PreservesWorkdirOnFailure()
    {
        var appRoot = TempDir();
        var isoDir  = TempDir();
        try
        {
            var isoPath   = Path.Combine(isoDir, "game.iso");
            var finalPath = Path.Combine(appRoot, "out.chd");
            File.WriteAllBytes(isoPath, [0x01]);

            bool ok = IsoChdWorkdir.Run(
                appRoot, FakeXform(), null, isoPath, finalPath,
                out var workdirUsed, out _, out _,
                executeTransformOverride: FakeFailure());

            Assert.False(ok);
            Assert.True(Directory.Exists(workdirUsed),
                "Workdir must be preserved on failure so the caller can log its path");
            Cleanup(workdirUsed);
        }
        finally { Cleanup(appRoot, isoDir); }
    }

    [Fact]
    public void IsoChdWorkdir_CleansWorkdirOnSuccess()
    {
        var appRoot = TempDir();
        var isoDir  = TempDir();
        try
        {
            var isoPath   = Path.Combine(isoDir, "game.iso");
            var finalPath = Path.Combine(appRoot, "out.chd");
            File.WriteAllBytes(isoPath, [0x01]);

            bool ok = IsoChdWorkdir.Run(
                appRoot, FakeXform(), null, isoPath, finalPath,
                out var workdirUsed, out _, out _,
                executeTransformOverride: FakeSuccess([0x01]));

            Assert.True(ok);
            Assert.False(Directory.Exists(workdirUsed),
                "Workdir must be deleted after a successful transform");
        }
        finally { Cleanup(appRoot, isoDir); }
    }

    [Fact]
    public void SingleIso_RetryNotBlockedByExistingPartialArchiveFile()
    {
        var appRoot = TempDir();
        var isoDir  = TempDir();
        var archive = TempDir();
        try
        {
            var isoPath    = Path.Combine(isoDir, "game.iso");
            var finalPath  = Path.Combine(archive, "game.chd");
            var goodContent = new byte[] { 0xAA, 0xBB, 0xCC };
            File.WriteAllBytes(isoPath, [0x01]);

            // Simulate a stale partial .chd left by a previous crashed run
            File.WriteAllBytes(finalPath, [0xFF, 0x00]);

            bool ok = IsoChdWorkdir.Run(
                appRoot, FakeXform(), null, isoPath, finalPath,
                out _, out _, out _,
                executeTransformOverride: FakeSuccess(goodContent));

            Assert.True(ok);
            // Stale content must be replaced by verified new output
            Assert.Equal(goodContent, File.ReadAllBytes(finalPath));
        }
        finally { Cleanup(appRoot, isoDir, archive); }
    }
}
