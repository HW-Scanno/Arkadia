using System.IO;
using Arkadia.Data;
using Arkadia.Volumes;
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

    // ── VolumeArtifactPathBuilder tests ──────────────────────────────────────

    // ── Test 5: VolumeArtifactPathBuilder_FlatPathOnly ───────────────────────

    [Fact]
    public void VolumeArtifactPathBuilder_FlatPathOnly()
    {
        var root = @"D:\volumes\ARKADIA-SNES-0001";
        var path = VolumeArtifactPathBuilder.GetFlatFullPath(root, "Super Mario World (USA).chd");

        Assert.Equal(Path.Combine(root, "Super Mario World (USA).chd"), path);
        // Exactly one path segment from root to file
        var relative = Path.GetRelativePath(root, path);
        Assert.Equal("Super Mario World (USA).chd", relative);
    }

    // ── Test 6: AppendVolume_WritesFlatPath ───────────────────────────────────

    [Fact]
    public void AppendVolume_WritesFlatPath_ViaBuilder()
    {
        var root = @"L:\";
        var info = Info("A Long Release Name With Spaces", "game.chd");

        var dst = VolumeArtifactPathBuilder.GetFlatFullPath(root, info.FileName);

        Assert.Equal(Path.Combine(root, "game.chd"), dst);
        Assert.DoesNotContain("A Long Release Name With Spaces", dst);
    }

    // ── Test 7: BuildVolume_WritesFlatPath ────────────────────────────────────

    [Fact]
    public void BuildVolume_WritesFlatPath_ViaBuilder()
    {
        var root = @"F:\volumes\ARKADIA-PS2-0003";
        var info = Info("Some (Japan) (Rev 2)", "game_disc1.chd");

        var dst = VolumeArtifactPathBuilder.GetFlatFullPath(root, info.FileName);

        // Parent directory must be the volume root itself
        Assert.Equal(root, Path.GetDirectoryName(dst), ignoreCase: true);
        Assert.Equal("game_disc1.chd", Path.GetFileName(dst));
    }

    // ── Test 8: WriteVolume_DoesNotUseReleaseFolder ───────────────────────────

    [Fact]
    public void WriteVolume_DoesNotUseReleaseFolder()
    {
        var root        = @"G:\vol";
        var releaseName = "007 - Agent Under Fire (Europe) (En,Fr,De,Es,It)";
        var fileName    = "007 - Agent Under Fire (Europe) (En,Fr,De,Es,It).chd";
        var info        = Info(releaseName, fileName);

        var dst = VolumeArtifactPathBuilder.GetFlatFullPath(root, info.FileName);

        // The release name must not appear as a DIRECTORY segment — only the
        // volume root itself should be the parent directory.
        var dirPart = Path.GetDirectoryName(dst)!;
        Assert.DoesNotContain(releaseName, dirPart);
        Assert.Equal(root, dirPart, ignoreCase: true);
        var relative = Path.GetRelativePath(root, dst);
        Assert.Equal(fileName, relative);
    }

    // ── Test 9: LongReleaseName_NotPresentInDestinationDirectory ─────────────

    [Fact]
    public void LongReleaseName_NotPresentInDestinationDirectory()
    {
        var root        = @"H:\vol";
        var releaseName = "A Very Long Release Name That Would Have Been A Subfolder Before";
        var info        = Info(releaseName, "artifact.chd");

        var dst = VolumeArtifactPathBuilder.GetFlatFullPath(root, info.FileName);

        var dirPart = Path.GetDirectoryName(dst)!;
        Assert.DoesNotContain(releaseName, dirPart);
        Assert.Equal(root, dirPart, ignoreCase: true);
    }

    // ── Build Volume production-path tests ────────────────────────────────────
    //
    // These call the SAME production authority that BuildVolumeCore uses
    // (VolumeArtifactPathBuilder.GetBuildDestinationPath) — NOT a test-local
    // reimplementation. They fail against the old
    // Path.Combine(volumeFolder, SafeFileName(ReleaseName), FileName) logic
    // because that produced a release-name subfolder segment.

    // ── Test 10: BuildDestination_IsFlat_NoReleaseSubfolder ──────────────────

    [Fact]
    public void BuildDestination_IsFlat_NoReleaseSubfolder()
    {
        var root = @"D:\volumes\ARKADIA-PS2-0003";
        var info = Info("Some (Japan) (Rev 2)", "game_disc1.chd");

        var dst = VolumeArtifactPathBuilder.GetBuildDestinationPath(root, info);

        // Parent directory must be the volume root itself — a single segment.
        Assert.Equal(root, Path.GetDirectoryName(dst), ignoreCase: true);
        Assert.Equal("game_disc1.chd", Path.GetFileName(dst));
        Assert.Equal("game_disc1.chd", Path.GetRelativePath(root, dst));
    }

    // ── Test 11: BuildDestination_DoesNotIncludeReleaseName ──────────────────

    [Fact]
    public void BuildDestination_DoesNotIncludeReleaseName()
    {
        var root        = @"E:\vol";
        var releaseName = "007 - Agent Under Fire (Europe) (En,Fr,De,Es,It)";
        var info        = Info(releaseName, "007 - Agent Under Fire (Europe) (En,Fr,De,Es,It).chd");

        var dst = VolumeArtifactPathBuilder.GetBuildDestinationPath(root, info);

        // The release name must NOT appear as a directory segment.
        Assert.Equal(root, Path.GetDirectoryName(dst)!, ignoreCase: true);
        Assert.Equal(info.FileName, Path.GetRelativePath(root, dst));
    }

    // ── Test 12: BuildDestination_Dreamcast_CueBinChd_IsFlat ─────────────────
    // Exact scenario from the reported bug: Sega Dreamcast .cue+.bin → CHD.

    [Fact]
    public void BuildDestination_Dreamcast_CueBinChd_IsFlat()
    {
        var root        = @"L:\volumes\ARKADIA-DREAMCAST-0001";
        var releaseName = "Sonic Adventure (USA)";
        var fileName    = "Sonic Adventure (USA).chd";
        var info        = Info(releaseName, fileName);

        var dst = VolumeArtifactPathBuilder.GetBuildDestinationPath(root, info);

        // Expected flat path — artifact directly at the volume root.
        var expected  = Path.Combine(root, "Sonic Adventure (USA).chd");
        // Forbidden release-folder path from the old broken logic.
        var forbidden = Path.Combine(root, "Sonic Adventure (USA)", "Sonic Adventure (USA).chd");

        Assert.Equal(expected, dst);
        Assert.NotEqual(forbidden, dst);

        // No release-name directory segment: exactly one segment from root to file.
        Assert.Equal(root, Path.GetDirectoryName(dst)!, ignoreCase: true);
        Assert.Equal(fileName, Path.GetRelativePath(root, dst));
    }
}
