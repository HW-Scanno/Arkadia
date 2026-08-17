using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Group Move Volume verification identity (V-G4): the relative-path-keyed expected snapshot, the normalized
/// path-key policy, same-FileName-across-different-leaves validity, and destination-vs-DB-hash verification in a
/// nested Group context. Pure — no UI, no DB. The Single filename-keyed behavior is covered separately and stays
/// unchanged (see <see cref="VolumeMoveFileVerifierTests"/>).
/// </summary>
public sealed class GroupVolumeMoveVerifierTests : IDisposable
{
    private readonly string _tmp;

    public GroupVolumeMoveVerifierTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ArkGVMove_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    private static string Sha1Hex(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private string WriteNested(string relative, byte[] content)
    {
        var path = Path.Combine(_tmp, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ── Path-key normalization ───────────────────────────────────────────────

    [Fact] // separators normalize to '/', leading/trailing trimmed — backslash and forward-slash forms match
    public void NormalizeRelativeKey_NormalizesSeparators()
    {
        Assert.Equal("firmware-crt/game.zip", VolumeMoveFileVerifier.NormalizeRelativeKey(@"firmware-crt\game.zip"));
        Assert.Equal("firmware-crt/game.zip", VolumeMoveFileVerifier.NormalizeRelativeKey("firmware-crt/game.zip"));
        Assert.Equal("firmware-crt/game.zip", VolumeMoveFileVerifier.NormalizeRelativeKey(@"\firmware-crt\game.zip\"));
    }

    // ── §24: same FileName under two different leaves is valid → two independent expected entries ──

    [Fact]
    public void BuildByRelativePath_SameFilenameDifferentLeaves_TwoEntries()
    {
        var shaA = new string('a', 40);
        var shaB = new string('b', 40);
        var (map, dup) = VolumeMoveFileVerifier.BuildExpectedSha1MapByRelativePath(new[]
        {
            ("firmware-crt/game.zip",     shaA, "c64-tosec-firmware-crt",     "da-a"),
            ("games-action-d64/game.zip", shaB, "c64-tosec-games-action-d64", "da-b"),
        });

        Assert.Null(dup);
        Assert.Equal(shaA, map["firmware-crt/game.zip"]);
        Assert.Equal(shaB, map["games-action-d64/game.zip"]);
        Assert.Equal(2, map.Count);                                   // NOT collapsed to one "game.zip"
    }

    [Fact] // lookup is case-insensitive on the relative path
    public void BuildByRelativePath_CaseInsensitiveLookup()
    {
        var sha = new string('c', 40);
        var (map, dup) = VolumeMoveFileVerifier.BuildExpectedSha1MapByRelativePath(new[]
        {
            ("Firmware-CRT/Game.ZIP", sha, "leaf", "da"),
        });

        Assert.Null(dup);
        Assert.Equal(sha, map["firmware-crt/game.zip"]);
    }

    [Fact] // §24/§7: two artifacts mapping to the SAME normalized relative path → block, naming BOTH
    public void BuildByRelativePath_DuplicateRelativePath_Blocks_NamingBoth()
    {
        var (map, dup) = VolumeMoveFileVerifier.BuildExpectedSha1MapByRelativePath(new[]
        {
            ("firmware-crt/game.zip", new string('a', 40), "leaf-a", "da-a"),
            (@"firmware-crt\game.zip", new string('b', 40), "leaf-b", "da-b"),   // same path, other separator
        });

        Assert.Empty(map);
        Assert.NotNull(dup);
        Assert.Equal("firmware-crt/game.zip", dup!.RelativePath);
        Assert.Equal("leaf-a", dup.DatLineIdA);
        Assert.Equal("da-a",   dup.DerivedArtifactIdA);
        Assert.Equal("leaf-b", dup.DatLineIdB);
        Assert.Equal("da-b",   dup.DerivedArtifactIdB);
    }

    [Fact] // empty/invalid SHA1 is excluded (⇒ legacy fallback) but never blocks
    public void BuildByRelativePath_InvalidHash_ExcludedNotBlocking()
    {
        var valid = new string('d', 40);
        var (map, dup) = VolumeMoveFileVerifier.BuildExpectedSha1MapByRelativePath(new[]
        {
            ("a/x.bin", "",             "leaf", "da1"),
            ("b/y.bin", "not-a-sha1",   "leaf", "da2"),
            ("c/z.bin", valid,          "leaf", "da3"),
        });

        Assert.Null(dup);
        Assert.False(map.ContainsKey("a/x.bin"));
        Assert.False(map.ContainsKey("b/y.bin"));
        Assert.Equal(valid, map["c/z.bin"]);
    }

    // ── §25/§26: same-named files in different leaves each verify against their OWN DB hash; source not read ──

    [Fact]
    public void VerifyFile_SameFilenameDifferentLeaves_EachVerifiesAgainstOwnHash_SourceNotHashed()
    {
        var contentA = new byte[] { 1, 2, 3, 4 };
        var contentB = new byte[] { 9, 9, 9, 9, 9 };
        var srcA = WriteNested("source/firmware-crt/game.zip",     contentA);
        var dstA = WriteNested("dest/firmware-crt/game.zip",       contentA);
        var srcB = WriteNested("source/games-action-d64/game.zip", contentB);
        var dstB = WriteNested("dest/games-action-d64/game.zip",   contentB);

        var (map, dup) = VolumeMoveFileVerifier.BuildExpectedSha1MapByRelativePath(new[]
        {
            ("firmware-crt/game.zip",     Sha1Hex(contentA), "leaf-a", "da-a"),
            ("games-action-d64/game.zip", Sha1Hex(contentB), "leaf-b", "da-b"),
        });
        Assert.Null(dup);

        var hashedPaths = new List<string>();
        string Spy(string path)
        {
            hashedPaths.Add(path);
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
        }

        bool okA = VolumeMoveFileVerifier.VerifyFile(
            srcA, dstA, contentA.Length, map["firmware-crt/game.zip"], out var mA, out var fA, hasher: Spy);
        bool okB = VolumeMoveFileVerifier.VerifyFile(
            srcB, dstB, contentB.Length, map["games-action-d64/game.zip"], out var mB, out var fB, hasher: Spy);

        Assert.True(okA, fA);
        Assert.True(okB, fB);
        Assert.Equal(VolumeMoveVerifyMethod.CatalogSha1, mA);
        Assert.Equal(VolumeMoveVerifyMethod.CatalogSha1, mB);

        // Only destinations were hashed — neither source was opened for verification.
        Assert.DoesNotContain(srcA, hashedPaths);
        Assert.DoesNotContain(srcB, hashedPaths);
        Assert.Contains(dstA, hashedPaths);
        Assert.Contains(dstB, hashedPaths);
    }
}
