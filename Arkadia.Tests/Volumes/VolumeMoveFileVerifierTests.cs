using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Tests for <see cref="VolumeMoveFileVerifier"/>: authoritative catalog-SHA1 destination verification
/// (no source re-read), per-file legacy source-vs-destination fallback, case-insensitive
/// expected-hash snapshot, and duplicate-filename blocking. Pure — no UI, no DB.
/// </summary>
public sealed class VolumeMoveFileVerifierTests : IDisposable
{
    private readonly string _tmp;

    public VolumeMoveFileVerifierTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ArkVolMove_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Sha1Hex(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_tmp, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ── 1. Authoritative: DB hash present + correct destination → success ──────

    [Fact]
    public void CatalogSha1_DestinationCorrect_Succeeds()
    {
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var src = Write("src.chd", content);
        var dst = Write("dst.chd", content);
        var expected = Sha1Hex(content);

        var ok = VolumeMoveFileVerifier.VerifyFile(
            src, dst, content.Length, expected, out var method, out var fail);

        Assert.True(ok, fail);
        Assert.Null(fail);
        Assert.Equal(VolumeMoveVerifyMethod.CatalogSha1, method);
    }

    // ── 2. Authoritative path never opens the source ──────────────────────────

    [Fact]
    public void CatalogSha1_DoesNotOpenSource()
    {
        var content = new byte[] { 9, 8, 7, 6 };
        var src = Write("src2.chd", content);
        var dst = Write("dst2.chd", content);
        var expected = Sha1Hex(content);

        var hashedPaths = new List<string>();
        string Spy(string path)
        {
            hashedPaths.Add(path);
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
        }

        var ok = VolumeMoveFileVerifier.VerifyFile(
            src, dst, content.Length, expected, out var method, out _, hasher: Spy);

        Assert.True(ok);
        Assert.Equal(VolumeMoveVerifyMethod.CatalogSha1, method);
        // Only the destination was hashed — the source path is never read.
        Assert.Equal(new[] { dst }, hashedPaths);
        Assert.DoesNotContain(src, hashedPaths);
    }

    // ── 3. Authoritative: wrong destination hash → fail ───────────────────────

    [Fact]
    public void CatalogSha1_WrongDestinationHash_Fails()
    {
        var src = Write("src3.chd", new byte[] { 1, 1, 1 });
        var dst = Write("dst3.chd", new byte[] { 2, 2, 2 });   // different content
        var expected = Sha1Hex(new byte[] { 1, 1, 1 });         // DB expects the source content

        var ok = VolumeMoveFileVerifier.VerifyFile(
            src, dst, 3, expected, out var method, out var fail);

        Assert.False(ok);
        Assert.Equal(VolumeMoveVerifyMethod.CatalogSha1, method);
        Assert.NotNull(fail);
    }

    // ── 4. Authoritative: wrong destination size → fail ───────────────────────

    [Fact]
    public void CatalogSha1_WrongDestinationSize_Fails()
    {
        var content = new byte[] { 4, 5, 6, 7 };
        var src = Write("src4.chd", content);
        var dst = Write("dst4.chd", content);
        var expected = Sha1Hex(content);

        // Claim a different expected size than the file on disk.
        var ok = VolumeMoveFileVerifier.VerifyFile(
            src, dst, content.Length + 100, expected, out _, out var fail);

        Assert.False(ok);
        Assert.NotNull(fail);
        Assert.Contains("size", fail!, StringComparison.OrdinalIgnoreCase);
    }

    // ── 5. Missing DB hash for a file → legacy fallback ───────────────────────

    [Fact]
    public void NoExpectedHash_UsesLegacyFallback()
    {
        var content = new byte[] { 3, 3, 3 };
        var src = Write("src5.bin", content);
        var dst = Write("dst5.bin", content);

        var ok = VolumeMoveFileVerifier.VerifyFile(
            src, dst, content.Length, expectedSha1: null, out var method, out var fail);

        Assert.True(ok, fail);
        Assert.Equal(VolumeMoveVerifyMethod.LegacyFallback, method);
    }

    // ── 6. Legacy fallback: identical files → success ─────────────────────────

    [Fact]
    public void LegacyFallback_IdenticalFiles_Succeeds()
    {
        var content = new byte[] { 7, 7, 7, 7 };
        var src = Write("src6.bin", content);
        var dst = Write("dst6.bin", content);

        var ok = VolumeMoveFileVerifier.VerifyFile(
            src, dst, content.Length, "", out var method, out var fail);   // empty ⇒ fallback

        Assert.True(ok, fail);
        Assert.Equal(VolumeMoveVerifyMethod.LegacyFallback, method);
    }

    // ── 7. Legacy fallback: different files → failure ─────────────────────────

    [Fact]
    public void LegacyFallback_DifferentFiles_Fails()
    {
        var src = Write("src7.bin", new byte[] { 1, 2, 3 });
        var dst = Write("dst7.bin", new byte[] { 4, 5, 6 });

        var ok = VolumeMoveFileVerifier.VerifyFile(
            src, dst, 3, null, out var method, out var fail);

        Assert.False(ok);
        Assert.Equal(VolumeMoveVerifyMethod.LegacyFallback, method);
        Assert.NotNull(fail);
    }

    // ── 8. Snapshot lookup is case-insensitive ────────────────────────────────

    [Fact]
    public void ExpectedSha1Map_LookupIsCaseInsensitive()
    {
        var sha = new string('a', 40);
        var (map, dup) = VolumeMoveFileVerifier.BuildExpectedSha1Map(new[]
        {
            ("Game.CHD", sha),
        });

        Assert.Null(dup);
        Assert.True(map.ContainsKey("game.chd"));
        Assert.True(map.ContainsKey("GAME.CHD"));
        Assert.Equal(sha, map["gAmE.cHd"]);
    }

    // ── 9. Duplicate filename (case-insensitive) in snapshot → block ──────────

    [Fact]
    public void ExpectedSha1Map_DuplicateFilename_CaseInsensitive_Blocks()
    {
        var (map, dup) = VolumeMoveFileVerifier.BuildExpectedSha1Map(new[]
        {
            ("game.chd", new string('a', 40)),
            ("GAME.CHD", new string('b', 40)),   // same filename, different case
        });

        Assert.Equal("GAME.CHD", dup);   // reported ambiguous filename
        Assert.Empty(map);               // caller must block; no silent overwrite
    }

    // ── Extra: empty / invalid hashes are excluded but do not block ───────────

    [Fact]
    public void ExpectedSha1Map_EmptyOrInvalidHash_ExcludedButNotBlocking()
    {
        var valid = new string('c', 40);
        var (map, dup) = VolumeMoveFileVerifier.BuildExpectedSha1Map(new[]
        {
            ("a.bin", ""),                 // empty → excluded
            ("b.bin", "not-a-real-sha1"),  // invalid → excluded
            ("c.bin", valid),              // kept
        });

        Assert.Null(dup);
        Assert.False(map.ContainsKey("a.bin"));
        Assert.False(map.ContainsKey("b.bin"));
        Assert.Equal(valid, map["c.bin"]);
    }
}
