using System;
using System.IO;
using System.Security.Cryptography;

namespace Arkadia.Volumes;

/// <summary>
/// Testable helpers for the Volume Append copy-and-verify pipeline.
/// </summary>
internal static class AppendVerifier
{
    /// <summary>
    /// Verifies that the destination file matches the DB-expected SHA1.
    /// Does NOT hash the source archive file.
    /// </summary>
    /// <param name="dst">Path of the freshly-copied destination file on the volume.</param>
    /// <param name="expectedSize">Expected byte count from <c>ArtifactBuildInfo.SizeBytes</c>.</param>
    /// <param name="expectedSha1">Expected SHA1 hex string from <c>ArtifactBuildInfo.ExpectedSha1</c>.</param>
    /// <param name="failReason">Human-readable failure description, or null on success.</param>
    /// <param name="logLines">
    /// Log text to append (includes VERIFY-DEST-START and VERIFY-DEST-OK/FAILED lines).
    /// Always non-null; empty string on pre-hash failure.
    /// </param>
    /// <param name="hasher">
    /// Injectable hasher for tests. Null uses the default SHA1 file stream.
    /// Signature: (filePath) → sha1HexLower.
    /// </param>
    internal static bool VerifyDestination(
        string                  dst,
        long                    expectedSize,
        string                  expectedSha1,
        out string?             failReason,
        out string              logLines,
        Func<string, string>?   hasher = null)
    {
        var name = Path.GetFileName(dst);

        if (expectedSha1.Length == 0)
        {
            failReason = $"DB expected hash is empty for {name}; cannot verify";
            logLines   = $"VERIFY-DEST-FAILED  {name}  expected=<empty>\n";
            return false;
        }

        var dstInfo = new FileInfo(dst);
        if (!dstInfo.Exists)
        {
            failReason = $"file missing after copy: {name}";
            logLines   = $"VERIFY-DEST-FAILED  {name}  missing\n";
            return false;
        }

        if (dstInfo.Length != expectedSize)
        {
            failReason = $"size mismatch: {name} (expected {expectedSize}, got {dstInfo.Length})";
            logLines   = $"VERIFY-DEST-FAILED  {name}  size-mismatch expected={expectedSize} actual={dstInfo.Length}\n";
            return false;
        }

        var computeHash = hasher ?? DefaultSha1;
        var shortExp    = Short(expectedSha1);

        var logBuf = $"VERIFY-DEST-START  {name}\n";
        var actual = computeHash(dst);
        var shortAct = Short(actual);

        if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
        {
            failReason = $"SHA1 mismatch: {name}";
            logLines   = logBuf + $"VERIFY-DEST-FAILED  {name}  expected={shortExp} actual={shortAct}\n";
            return false;
        }

        failReason = null;
        logLines   = logBuf + $"VERIFY-DEST-OK  {name}  expected={shortExp} actual={shortAct}\n";
        return true;
    }

    /// <summary>
    /// Computes remaining ETA seconds for a two-phase copy+verify operation.
    /// Uses combined throughput so ETA stays non-zero during the verify phase.
    /// </summary>
    internal static double CalculateEtaSeconds(
        long copiedBytes, long verifiedBytes, long totalBytes, TimeSpan elapsed)
    {
        if (totalBytes <= 0) return 0;
        double totalWork = totalBytes * 2.0;
        double doneWork  = copiedBytes + verifiedBytes;
        double speedBps  = elapsed.TotalSeconds > 0.5 ? doneWork / elapsed.TotalSeconds : 0;
        return speedBps > 0 ? Math.Max(0, totalWork - doneWork) / speedBps : 0;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static string DefaultSha1(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    private static string Short(string sha1)
        => sha1.Length >= 8 ? sha1[..8] : sha1;
}
