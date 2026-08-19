using System;
using System.IO;
using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>The per-platform staging probe: a leaf dat line has staging when its
/// <c>staging/&lt;platform&gt;/&lt;leafId&gt;</c> root contains any file (short-circuit, no recursion cost beyond
/// the first hit). Missing trees yield an empty set.</summary>
public sealed class StagingSnapshotTests : IDisposable
{
    private readonly string _root;

    public StagingSnapshotTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkStaging_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void MakeLeaf(string platform, string leafId, bool withFile, bool nested = false)
    {
        var dir = Path.Combine(_root, "staging", platform, leafId);
        if (nested) dir = Path.Combine(dir, "SafeName");
        Directory.CreateDirectory(dir);
        if (withFile) File.WriteAllText(Path.Combine(dir, "a.bin"), "x");
    }

    [Fact]
    public void MissingStagingRoot_Empty()
        => Assert.Empty(StagingSnapshot.LeafIdsWithStaging(_root, "c64"));

    [Fact]
    public void OnlyNonEmptyLeaves_AreReported()
    {
        MakeLeaf("c64", "leaf-with",    withFile: true);
        MakeLeaf("c64", "leaf-empty",   withFile: false);
        MakeLeaf("c64", "leaf-nested",  withFile: true, nested: true);   // file under a subfolder still counts

        var set = StagingSnapshot.LeafIdsWithStaging(_root, "c64");

        Assert.Contains("leaf-with", set);
        Assert.Contains("leaf-nested", set);
        Assert.DoesNotContain("leaf-empty", set);   // directory present but no file → not staging
    }

    [Fact]
    public void OtherPlatform_NotIncluded()
    {
        MakeLeaf("nes", "nes-leaf", withFile: true);
        Assert.Empty(StagingSnapshot.LeafIdsWithStaging(_root, "c64"));
    }
}
