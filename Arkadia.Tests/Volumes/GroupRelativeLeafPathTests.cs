using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// The authoritative Group-relative leaf-segment policy (V-G3, §24) and the additive Group path-builder methods,
/// plus a guard that the Single flat methods are untouched (§25). Pure — no DB, no filesystem.
/// </summary>
public sealed class GroupRelativeLeafPathTests
{
    // ── Segment derivation: prefix-strip from the TECHNICAL ids only, exact prefix (not string.Replace) ──

    [Fact] // the leaf id begins with "<groupId>-" → segment is the suffix
    public void Segment_StripsGroupPrefix()
        => Assert.Equal("firmware-crt", GroupRelativeLeafPath.Segment("c64-tosec", "c64-tosec-firmware-crt"));

    [Fact] // a deeper nested id keeps everything after the group prefix as one atomic segment
    public void Segment_KeepsInnerHyphens()
        => Assert.Equal("games-strategy-d64",
            GroupRelativeLeafPath.Segment("c64-tosec", "c64-tosec-games-strategy-d64"));

    [Fact] // a manually-overridden id that does NOT follow the convention → the full id is the segment
    public void Segment_NonPrefixed_UsesFullId()
        => Assert.Equal("weird-manual-id", GroupRelativeLeafPath.Segment("c64-tosec", "weird-manual-id"));

    [Fact] // exact prefix test — "c64-tosecX" must NOT be treated as prefixed by "c64-tosec"
    public void Segment_RequiresHyphenBoundary()
        => Assert.Equal("c64-toseczz", GroupRelativeLeafPath.Segment("c64-tosec", "c64-toseczz"));

    // ── BuildMap: validated, deterministic DatLineId → segment map ──

    [Fact] // happy path: every leaf resolves; no error
    public void BuildMap_Ok()
    {
        var (map, err) = GroupRelativeLeafPath.BuildMap("c64-tosec",
            new[] { "c64-tosec-firmware-crt", "c64-tosec-games-action-d64" });

        Assert.Null(err);
        Assert.Equal("firmware-crt",     map["c64-tosec-firmware-crt"]);
        Assert.Equal("games-action-d64", map["c64-tosec-games-action-d64"]);
    }

    [Fact] // §24: two leaves colliding to one segment (case-insensitive) BLOCK, naming both — no auto-suffix
    public void BuildMap_Collision_Blocks_NamingBoth()
    {
        // The prefixed leaf and a manual override that resolves to the same segment collide.
        var (map, err) = GroupRelativeLeafPath.BuildMap("c64-tosec",
            new[] { "c64-tosec-firmware-crt", "firmware-crt" });

        Assert.Empty(map);
        Assert.NotNull(err);
        Assert.Contains("c64-tosec-firmware-crt", err);
        Assert.Contains("firmware-crt",           err);
    }

    [Fact] // an empty segment (leaf id is exactly "<groupId>-", stripping to nothing) is unsafe → BLOCK
    public void BuildMap_EmptySegment_Blocks()
    {
        var (map, err) = GroupRelativeLeafPath.BuildMap("c64-tosec", new[] { "c64-tosec-" });
        Assert.Empty(map);
        Assert.NotNull(err);
    }

    // ── Path builder: Group nests one level under the leaf segment; SAME filename across DIFFERENT leaves is valid ──

    [Fact] // §24: full nested relative path is the identity — same file under two leaves → two distinct paths
    public void GroupBuildDestination_SameFilename_DifferentLeaves_DistinctPaths()
    {
        var root = Path.Combine("X:", "volumes", "c64-tosec-001");
        var info = new ArtifactBuildInfo
        {
            DerivedArtifactId = "da", ReleaseName = "Game", FileName = "game.zip",
            RelativePath = "archive/c64/x/game.zip", SizeBytes = 1, ExpectedSha1 = "sha",
        };

        var a = VolumeArtifactPathBuilder.GetGroupBuildDestinationPath(root, "firmware-crt", info);
        var b = VolumeArtifactPathBuilder.GetGroupBuildDestinationPath(root, "games-action-d64", info);

        Assert.NotEqual(a, b);
        Assert.Equal(Path.Combine(root, "firmware-crt", "game.zip"),     a);
        Assert.Equal(Path.Combine(root, "games-action-d64", "game.zip"), b);
    }

    [Fact] // §25 regression: the Single flat methods stay flat (no leaf segment)
    public void SingleFlatMethods_Unchanged()
    {
        var root = Path.Combine("X:", "volumes", "c64-redump-001");
        var info = new ArtifactBuildInfo
        {
            DerivedArtifactId = "da", ReleaseName = "Game", FileName = "game.zip",
            RelativePath = "archive/c64/x/game.zip", SizeBytes = 1, ExpectedSha1 = "sha",
        };

        Assert.Equal(Path.Combine(root, "game.zip"), VolumeArtifactPathBuilder.GetFlatFullPath(root, "game.zip"));
        Assert.Equal(Path.Combine(root, "game.zip"), VolumeArtifactPathBuilder.GetBuildDestinationPath(root, info));
    }
}
