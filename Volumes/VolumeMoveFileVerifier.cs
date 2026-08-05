using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Arkadia.Volumes;

/// <summary>Which strategy verified a moved file.</summary>
public enum VolumeMoveVerifyMethod
{
    /// <summary>Destination verified against the authoritative catalog SHA1 (source not re-read).</summary>
    CatalogSha1,
    /// <summary>No authoritative hash for this file — historical source-vs-destination comparison.</summary>
    LegacyFallback,
}

/// <summary>
/// Per-file verification for <b>Move Volume</b>. When the catalog holds an authoritative SHA1 for the
/// artifact filename, the destination is verified against that DB hash <b>without re-reading the
/// source</b> (mirrors Append/Fillback via <see cref="AppendVerifier.VerifyDestination"/>). Only files
/// with no authoritative hash (legacy / untracked / marker / empty hash) fall back to the historical
/// source-vs-destination comparison for that single file. Pure and DB-free: the expected-hash
/// snapshot is built by the caller <i>before</i> the copy starts, so no DB read happens during the
/// transfer. This helper is used exclusively by Move Volume and touches no other workflow.
/// </summary>
public static class VolumeMoveFileVerifier
{
    /// <summary>
    /// Builds the case-insensitive <c>filename → authoritative SHA1</c> snapshot from the volume's
    /// assigned artifacts. Only non-empty, syntactically valid SHA1s are kept; a file without a valid
    /// hash is simply absent (⇒ per-file legacy fallback). Returns a non-null
    /// <c>DuplicateFileName</c> when two assigned artifacts share a filename case-insensitively — the
    /// caller MUST block the move before copying (never overwrite silently).
    /// </summary>
    public static (Dictionary<string, string> Map, string? DuplicateFileName) BuildExpectedSha1Map(
        IEnumerable<(string FileName, string ExpectedSha1)> assignedArtifacts)
    {
        var map  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fileName, sha1) in assignedArtifacts)
        {
            if (string.IsNullOrEmpty(fileName)) continue;

            // Duplicate filename among assigned artifacts (case-insensitive) → blocking, regardless
            // of whether either hash is present. The caller aborts before any copy.
            if (!seen.Add(fileName))
                return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), fileName);

            if (IsValidSha1(sha1))
                map[fileName] = sha1;
        }

        return (map, null);
    }

    /// <summary>
    /// Verifies one already-copied file. When <paramref name="expectedSha1"/> is a valid catalog hash
    /// the authoritative path runs (destination existence + size + hash vs DB; the source is never
    /// opened). Otherwise the historical source-vs-destination comparison runs for that single file.
    /// </summary>
    public static bool VerifyFile(
        string  srcPath,
        string  dstPath,
        long    expectedSize,
        string? expectedSha1,
        out VolumeMoveVerifyMethod method,
        out string? failReason,
        Func<string, string>? hasher = null)
    {
        var hash = hasher ?? DefaultSha1;

        if (IsValidSha1(expectedSha1))
        {
            // Authoritative: reuse the exact verifier Append/Fillback use — destination only.
            method = VolumeMoveVerifyMethod.CatalogSha1;
            return AppendVerifier.VerifyDestination(
                dstPath, expectedSize, expectedSha1!, out failReason, out _, hasher);
        }

        // Legacy/untracked fallback: existence + size + source-vs-destination hash comparison.
        method = VolumeMoveVerifyMethod.LegacyFallback;
        var name    = Path.GetFileName(dstPath);
        var dstInfo = new FileInfo(dstPath);

        if (!dstInfo.Exists)
        {
            failReason = $"file missing after copy: {name}";
            return false;
        }
        if (dstInfo.Length != expectedSize)
        {
            failReason = $"size mismatch: {name} (expected {expectedSize}, got {dstInfo.Length})";
            return false;
        }

        var srcSha1 = hash(srcPath);
        var dstSha1 = hash(dstPath);
        if (!string.Equals(srcSha1, dstSha1, StringComparison.OrdinalIgnoreCase))
        {
            failReason = $"SHA1 mismatch (source vs destination): {name}";
            return false;
        }

        failReason = null;
        return true;
    }

    /// <summary>True for a 40-character hex SHA1 string (either case). Empty/short/non-hex ⇒ false.</summary>
    public static bool IsValidSha1(string? sha1)
    {
        if (sha1 is null || sha1.Length != 40) return false;
        foreach (var ch in sha1)
            if (!Uri.IsHexDigit(ch)) return false;
        return true;
    }

    private static string DefaultSha1(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }
}
