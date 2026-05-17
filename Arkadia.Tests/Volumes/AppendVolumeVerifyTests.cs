using System;
using System.IO;
using System.Security.Cryptography;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Tests for AppendVerifier: destination-vs-DB hash verification and ETA calculation.
/// No UI dependencies — all logic is in the static AppendVerifier helper.
/// </summary>
public sealed class AppendVolumeVerifyTests : IDisposable
{
    private readonly string _tmp;

    public AppendVolumeVerifyTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ArkAppendVerify_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Sha1Hex(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_tmp, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ── Test 1: destination matches DB hash → success ─────────────────────────

    [Fact]
    public void DestinationMatchesDbHash_CommitsVolumeArtifact()
    {
        var content  = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var dst      = WriteFile("game.chd", content);
        var expected = Sha1Hex(content);

        var ok = AppendVerifier.VerifyDestination(
            dst, content.Length, expected,
            out var failReason, out var logLines);

        Assert.True(ok, failReason);
        Assert.Null(failReason);
        Assert.Contains("VERIFY-DEST-OK", logLines);
        Assert.Contains(expected[..8], logLines);
    }

    // ── Test 2: destination hash differs from DB hash → fail ──────────────────

    [Fact]
    public void DestinationHashDiffersFromDbHash_Fails()
    {
        var content      = new byte[] { 0xAA, 0xBB, 0xCC };
        var dst          = WriteFile("game2.chd", content);
        var wrongExpected = Sha1Hex(new byte[] { 0xFF, 0xFE, 0xFD }); // different bytes

        var ok = AppendVerifier.VerifyDestination(
            dst, content.Length, wrongExpected,
            out var failReason, out var logLines);

        Assert.False(ok);
        Assert.NotNull(failReason);
        Assert.Contains("SHA1 mismatch", failReason);
        Assert.Contains("VERIFY-DEST-FAILED", logLines);
    }

    // ── Test 3: src == dst bytes but DB hash is different → must fail ─────────
    // Proves the DB hash is the source of truth, not a src-vs-dst comparison.

    [Fact]
    public void SourceAndDestinationSameButDbHashDifferent_Fails()
    {
        var sharedContent = new byte[] { 0x11, 0x22, 0x33 };
        // src and dst have identical content
        WriteFile("src.chd",  sharedContent);
        var dst = WriteFile("dst.chd", sharedContent);

        // DB says a completely different sha1 is expected
        var wrongDbSha1 = Sha1Hex(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var ok = AppendVerifier.VerifyDestination(
            dst, sharedContent.Length, wrongDbSha1,
            out var failReason, out var logLines);

        // Must fail even though src == dst
        Assert.False(ok, "src == dst is NOT sufficient; DB hash is the authority");
        Assert.Contains("SHA1 mismatch", failReason);
        Assert.Contains("VERIFY-DEST-FAILED", logLines);
    }

    // ── Test 4: source is never hashed in default mode ────────────────────────
    // The injectable hasher is called exactly once — for dst — never for src.

    [Fact]
    public void SourceHashNotComputedInDefaultMode()
    {
        var content  = new byte[] { 0x55, 0x66, 0x77 };
        var dst      = WriteFile("dest.chd", content);
        var expected = Sha1Hex(content);

        int hasherCalls = 0;
        string Spy(string path)
        {
            hasherCalls++;
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
        }

        var ok = AppendVerifier.VerifyDestination(
            dst, content.Length, expected,
            out _, out _,
            hasher: Spy);

        Assert.True(ok);
        // Hasher is called exactly once: only for dst, never for any source file.
        // Old code called it twice (srcSha1 + dstSha1); new code calls it once.
        Assert.Equal(1, hasherCalls);
    }

    // ── Test 5: ETA is non-zero when copy done but verify still pending ───────

    [Fact]
    public void Eta_CopyDoneVerifyPending_NotZero()
    {
        const long total    = 1_000_000_000L; // 1 GB
        long copiedBytes    = total;           // copy phase complete
        long verifiedBytes  = 0;              // verify not started
        var  elapsed        = TimeSpan.FromSeconds(10); // spent 10 s copying

        double etaSec = AppendVerifier.CalculateEtaSeconds(
            copiedBytes, verifiedBytes, total, elapsed);

        // With 1 GB copied in 10 s, verify of 1 GB should project ~10 s remaining.
        Assert.True(etaSec > 0,
            $"ETA must not be zero while verify is still pending (got {etaSec:F1}s)");
    }
}
