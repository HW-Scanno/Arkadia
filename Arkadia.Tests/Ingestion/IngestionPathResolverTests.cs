using System.IO;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// The shared release-child path seam <see cref="IngestionPaths.TryResolveReleaseChild"/>: preserves a DAT
/// <c>rom.name</c> as a relative path inside the release, treats both <c>\</c> and <c>/</c> as logical
/// separators, and rejects any escaping/absolute name before it can reach the filesystem. Pure — no I/O.
/// </summary>
public sealed class IngestionPathResolverTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "ArkRel", "release");   // need not exist; GetFullPath is textual

    [Fact]  // flat name resolves directly under the root (unchanged from historical Path.Combine)
    public void Flat_ResolvesUnderRoot()
    {
        Assert.True(IngestionPaths.TryResolveReleaseChild(Root, "game.bin", out var p));
        Assert.Equal(Path.Combine(Root, "game.bin"), p);
    }

    [Fact]  // backslash-separated nested path → native nested path below the root
    public void NestedBackslash_Resolves()
    {
        Assert.True(IngestionPaths.TryResolveReleaseChild(Root, "TIME-PILOT\\assets\\bank001.au", out var p));
        Assert.EndsWith(Path.Combine("TIME-PILOT", "assets", "bank001.au"), p);
        Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, p);
    }

    [Fact]  // forward-slash nested path resolves identically (cross-platform separator policy)
    public void NestedForwardSlash_Resolves()
    {
        Assert.True(IngestionPaths.TryResolveReleaseChild(Root, "TIME-PILOT/assets/bank001.au", out var p));
        Assert.EndsWith(Path.Combine("TIME-PILOT", "assets", "bank001.au"), p);
    }

    [Fact]  // mixed separators in one name both count as logical separators
    public void MixedSeparators_Resolve()
    {
        Assert.True(IngestionPaths.TryResolveReleaseChild(Root, "folder\\sub/file.bin", out var p));
        Assert.EndsWith(Path.Combine("folder", "sub", "file.bin"), p);
    }

    [Fact]  // dot-prefixed segments (.META / .THUMB) are valid names, not traversal
    public void DotPrefixedSegments_AreValid()
    {
        Assert.True(IngestionPaths.TryResolveReleaseChild(Root, "LAUNCHER.DATA\\.META\\TIME-PILOT.INF", out var p));
        Assert.EndsWith(Path.Combine("LAUNCHER.DATA", ".META", "TIME-PILOT.INF"), p);
    }

    [Theory]  // every escaping / absolute form is rejected, both separator styles
    [InlineData("..\\evil.bin")]
    [InlineData("../evil.bin")]
    [InlineData("../../evil.bin")]
    [InlineData("..\\..\\evil.bin")]
    [InlineData("dir\\..\\..\\evil.bin")]
    [InlineData("C:\\evil.bin")]
    [InlineData("C:/evil.bin")]
    [InlineData("\\evil.bin")]
    [InlineData("/evil.bin")]
    [InlineData("\\\\server\\share\\evil.bin")]
    [InlineData("")]
    [InlineData("   ")]
    public void EscapingOrAbsolute_Rejected(string romName)
        => Assert.False(IngestionPaths.TryResolveReleaseChild(Root, romName, out _));

    [Fact]  // a sibling directory whose name merely starts with the root is NOT contained (robust boundary)
    public void SiblingPrefixDirectory_NotConfusedForContainment()
    {
        // "..\release-evil\x" would land in a sibling of the release root; it must be rejected, not accepted by
        // a naive textual prefix check. (Here it is rejected earlier by the ".." segment, proving no escape.)
        Assert.False(IngestionPaths.TryResolveReleaseChild(Root, "..\\release-evil\\x.bin", out _));
    }

    [Fact]  // throwing form throws for an unsafe name (write sites rely on this to fail the target)
    public void ResolveReleaseChild_Throws_OnEscape()
        => Assert.Throws<IOException>(() => IngestionPaths.ResolveReleaseChild(Root, "..\\evil.bin"));

    [Fact]  // throwing form returns the safe path for a valid nested name
    public void ResolveReleaseChild_Returns_ForValid()
        => Assert.EndsWith(Path.Combine("a", "b.bin"), IngestionPaths.ResolveReleaseChild(Root, "a/b.bin"));
}
