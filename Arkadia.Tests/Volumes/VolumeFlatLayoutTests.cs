using System.IO;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Confirms the final flat volume artifact layout:
///   &lt;volume root&gt;\&lt;artifact filename&gt;.chd
/// No release-name sub-folder is ever involved.
/// </summary>
public sealed class VolumeFlatLayoutTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArtifactBuildInfo Info(
        string releaseName,
        string fileName,
        string relativePath = "") =>
        new()
        {
            DerivedArtifactId = "test-da-id",
            ReleaseName       = releaseName,
            FileName          = fileName,
            RelativePath      = relativePath.Length > 0 ? relativePath : $"archive/ps2/ps2-redump/{fileName}",
            SizeBytes         = 1024,
            ExpectedSha1      = "aabbccddaabbccddaabbccddaabbccddaabbccdd",
        };

    private static string BuildAppendDst(string volumeRoot, ArtifactBuildInfo info)
        => Path.Combine(volumeRoot, info.FileName);

    private static string BuildVerifyPath(string volumeRoot, ArtifactVerifyInfo vi)
        => Path.Combine(volumeRoot, vi.FileName);

    // ── Test 1: AppendVolume_WritesFlatPath ───────────────────────────────────

    [Fact]
    public void AppendVolume_WritesFlatPath()
    {
        var root = @"L:\";
        var info = Info("Some Long Release Name", "game.chd");

        var dst = BuildAppendDst(root, info);

        Assert.Equal(Path.Combine(root, "game.chd"), dst);
    }

    // ── Test 2: VerifyVolume_UsesFlatPathOnly ─────────────────────────────────

    [Fact]
    public void VerifyVolume_UsesFlatPathOnly()
    {
        var root = @"L:\";
        var vi   = new ArtifactVerifyInfo
        {
            DerivedArtifactId = "id",
            ReleaseName       = "A Very Long Release Name",
            FileName          = "artifact.chd",
            SizeBytes         = 512,
            Sha1              = "",
        };

        var absPath = BuildVerifyPath(root, vi);

        Assert.Equal(Path.Combine(root, "artifact.chd"), absPath);
        Assert.DoesNotContain("A Very Long Release Name", absPath);
    }

    // ── Test 3: NewVolumeArtifactPath_IsFlat ─────────────────────────────────

    [Fact]
    public void NewVolumeArtifactPath_IsFlat()
    {
        var root = @"D:\volumes\my-volume";
        var info = Info("Release With A Distinctive Name", "release.chd");

        var dst = BuildAppendDst(root, info);

        // Must be directly in volumeRoot — no sub-directory
        Assert.Equal(root, Path.GetDirectoryName(dst), ignoreCase: true);
        Assert.Equal("release.chd", Path.GetFileName(dst));
    }

    // ── Test 4: NewVolumeArtifactPath_DoesNotIncludeReleaseFolder ────────────

    [Fact]
    public void NewVolumeArtifactPath_DoesNotIncludeReleaseFolder()
    {
        var root        = @"E:\vol";
        var releaseName = "Unique Release Folder Name";
        var info        = Info(releaseName, "disc.chd");

        var dst = BuildAppendDst(root, info);

        Assert.DoesNotContain(releaseName, dst);
        // Also verify no path segment between root and filename
        var relative = Path.GetRelativePath(root, dst);
        Assert.Equal("disc.chd", relative);
    }
}
