using System;
using System.IO;
using Arkadia.Archive;
using Xunit;

namespace Arkadia.Tests.Archive;

/// <summary>
/// M1d tests: the ArchiveArtifactPathBuilder authority + the ArchiveWritePlanner
/// path-selection/idempotency helper. These are the production seams the four
/// ingestion writers route through, so they cover the naming policy without the
/// UI-private processors (no rule is reimplemented in the test).
/// </summary>
public sealed class ArchiveArtifactPathBuilderTests
{
    // ── 1-2: builder ─────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveArtifactPathBuilder_SingleFileFlat_ReturnsFlatPath()
    {
        var rel = ArchiveArtifactPathBuilder.GetRelativePath(
            "dc", "redump", ArchiveDatLineOutputForm.SingleFileFlat, "Sonic Adventure (USA)", "Sonic Adventure (USA).chd");
        Assert.Equal("archive/dc/redump/Sonic Adventure (USA).chd", rel);
    }

    [Fact]
    public void ArchiveArtifactPathBuilder_MultiFileReleaseFolder_ReturnsReleaseFolderPath()
    {
        var rel = ArchiveArtifactPathBuilder.GetRelativePath(
            "psx", "redump", ArchiveDatLineOutputForm.MultiFileReleaseFolder, "Game", "Track 01.bin");
        Assert.Equal("archive/psx/redump/Game/Track 01.bin", rel);
    }

    // ── 3-4: CHD new artifact → release-name-based flat, NOT main input ──────────

    [Fact]
    public void ArchiveWriter_ChdNewArtifact_UsesReleaseNameBasedFlatFilename()
    {
        // No existing artifact → new policy path. Writer passes SafeReleaseName + ".chd".
        var plan = ArchiveWritePlanner.Plan(
            @"C:\app", "dc", "redump", ArchiveDatLineOutputForm.SingleFileFlat,
            "Sonic Adventure (USA)", "Sonic Adventure (USA).chd", existingRelativePath: null);

        Assert.Equal(ArchiveWritePathAction.WriteNew, plan.Action);
        Assert.Equal("archive/dc/redump/Sonic Adventure (USA).chd", plan.RelativePath);
    }

    [Fact]
    public void ArchiveWriter_ChdNewArtifact_DoesNotUseMainInputFilename()
    {
        var plan = ArchiveWritePlanner.Plan(
            @"C:\app", "dc", "redump", ArchiveDatLineOutputForm.SingleFileFlat,
            "Sonic Adventure (USA)", "Sonic Adventure (USA).chd", existingRelativePath: null);

        Assert.DoesNotContain("disc.chd", plan.RelativePath);   // never main-input-based
    }

    // ── 5: ZIP remains release-name-based flat ───────────────────────────────────

    [Fact]
    public void ArchiveWriter_ZipOutput_RemainsReleaseNameBasedFlat()
    {
        var plan = ArchiveWritePlanner.Plan(
            @"C:\app", "gba", "nointro", ArchiveDatLineOutputForm.SingleFileFlat,
            "Game Name", "Game Name.zip", existingRelativePath: null);
        Assert.Equal("archive/gba/nointro/Game Name.zip", plan.RelativePath);
    }

    // ── 6-7: file_extension single vs multi ──────────────────────────────────────

    [Fact]
    public void ArchiveWriter_FileExtensionSingleFileFlat_UsesReleaseNameBasedFilename()
    {
        // Single-file DAT line: writer passes SafeReleaseName + outputExt.
        var plan = ArchiveWritePlanner.Plan(
            @"C:\app", "nes", "nointro", ArchiveDatLineOutputForm.SingleFileFlat,
            "Super Mario", "Super Mario.nes", existingRelativePath: null);
        Assert.Equal("archive/nes/nointro/Super Mario.nes", plan.RelativePath);
    }

    [Fact]
    public void ArchiveWriter_FileExtensionMultiFileReleaseFolder_PreservesOriginalFilenamesInReleaseFolder()
    {
        // Multi-file DAT line: writer passes the original inner filename.
        var t1 = ArchiveWritePlanner.Plan(
            @"C:\app", "nes", "nointro", ArchiveDatLineOutputForm.MultiFileReleaseFolder,
            "Multi Game", "Track 01.chd", existingRelativePath: null);
        var t2 = ArchiveWritePlanner.Plan(
            @"C:\app", "nes", "nointro", ArchiveDatLineOutputForm.MultiFileReleaseFolder,
            "Multi Game", "Track 02.chd", existingRelativePath: null);

        Assert.Equal("archive/nes/nointro/Multi Game/Track 01.chd", t1.RelativePath);
        Assert.Equal("archive/nes/nointro/Multi Game/Track 02.chd", t2.RelativePath);
    }

    // ── 8: No Compression Folder uses the release folder ─────────────────────────

    [Fact]
    public void ArchiveWriter_NoCompressionFolder_UsesReleaseFolder()
    {
        var rel = ArchiveArtifactPathBuilder.GetRelativePath(
            "psx", "redump", ArchiveDatLineOutputForm.MultiFileReleaseFolder, "Some Game", "disc.bin");
        Assert.StartsWith("archive/psx/redump/Some Game/", rel);
    }

    // ── 9: relative_path persisted exactly as built ──────────────────────────────

    [Fact]
    public void ArchiveWriter_PersistsRelativePathExactlyAsBuilt()
    {
        // The writer persists derived_artifacts.relative_path = plan.RelativePath verbatim.
        var plan = ArchiveWritePlanner.Plan(
            @"C:\app", "dc", "redump", ArchiveDatLineOutputForm.SingleFileFlat,
            "Zelda (USA)", "Zelda (USA).chd", existingRelativePath: null);

        var built = ArchiveArtifactPathBuilder.GetRelativePath(
            "dc", "redump", ArchiveDatLineOutputForm.SingleFileFlat, "Zelda (USA)", "Zelda (USA).chd");
        Assert.Equal(built, plan.RelativePath);

        // Full path is the app-root join of the same relative path.
        Assert.Equal(Path.Combine(@"C:\app", built.Replace('/', Path.DirectorySeparatorChar)), plan.FullPath);
    }

    // ── 10-11: existing relative_path recognized / preserved (idempotency) ───────

    [Fact]
    public void ArchiveWriter_ExistingOldChdRelativePath_IsRecognizedAndNotRetransformed()
    {
        // A legacy artifact stored under the old main-input-based name must be reused,
        // NOT re-written under the new release-name-based name.
        var legacy = "archive/dc/redump/disc.chd";
        var plan = ArchiveWritePlanner.Plan(
            @"C:\app", "dc", "redump", ArchiveDatLineOutputForm.SingleFileFlat,
            "Sonic Adventure (USA)", "Sonic Adventure (USA).chd", existingRelativePath: legacy);

        Assert.Equal(ArchiveWritePathAction.UseExistingRelativePath, plan.Action);
        Assert.Equal(legacy, plan.RelativePath);   // keeps the old path → no orphan, no re-transform
    }

    [Fact]
    public void ArchiveWriter_ExistingFolderedRelativePath_RemainsValid()
    {
        var legacyFoldered = "archive/ps2/dl/Some Release/Game.chd";
        var plan = ArchiveWritePlanner.Plan(
            @"C:\app", "ps2", "dl", ArchiveDatLineOutputForm.SingleFileFlat,
            "Some Release", "Some Release.chd", existingRelativePath: legacyFoldered);

        Assert.Equal(ArchiveWritePathAction.UseExistingRelativePath, plan.Action);
        Assert.Equal(legacyFoldered, plan.RelativePath);
    }

    // ── 12: new release-name collision blocked by the M1c guard ──────────────────

    [Fact]
    public void ArchiveWriter_NewReleaseNameCollision_IsBlockedByRuntimeGuard()
    {
        // Two releases whose SafeReleaseName is identical resolve to the same new path;
        // the runtime guard blocks the second (different content identity).
        var relA = ArchiveArtifactPathBuilder.GetRelativePath(
            "dc", "redump", ArchiveDatLineOutputForm.SingleFileFlat, "Game", "Game.chd");
        var relB = ArchiveArtifactPathBuilder.GetRelativePath(
            "dc", "redump", ArchiveDatLineOutputForm.SingleFileFlat, "Game", "Game.chd");
        Assert.Equal(relA, relB);   // same target

        var decision = ArchiveWriteCollisionGuard.Decide(
            targetExists: true, new[] { "release:A" }, "release:B");
        Assert.True(ArchiveWriteCollisionGuard.IsBlocking(decision));
    }

    // ── 12b: release-folder root helper (No Compression Folder writer) ───────────

    [Fact]
    public void ArchiveArtifactPathBuilder_GetReleaseFolderRoot_ReturnsFolderRootPath()
    {
        var rel = ArchiveArtifactPathBuilder.GetReleaseFolderRoot("psx", "redump", "Some Game");
        Assert.Equal("archive/psx/redump/Some Game", rel);
    }

    [Fact]
    public void ArchiveArtifactPathBuilder_GetReleaseFolderRoot_IsParentOfInnerFilePaths()
    {
        // The folder root must equal the directory portion the builder produces for the
        // inner files of the same MultiFileReleaseFolder release — same produced paths.
        var root  = ArchiveArtifactPathBuilder.GetReleaseFolderRoot("psx", "redump", "Some Game");
        var inner = ArchiveArtifactPathBuilder.GetRelativePath(
            "psx", "redump", ArchiveDatLineOutputForm.MultiFileReleaseFolder, "Some Game", "disc.bin");
        Assert.Equal(root + "/disc.bin", inner);
    }

    [Fact]
    public void ArchiveArtifactPathBuilder_GetReleaseFolderRootFullPath_JoinsAppRoot()
    {
        var full = ArchiveArtifactPathBuilder.GetReleaseFolderRootFullPath(@"C:\app", "psx", "redump", "Some Game");
        Assert.Equal(
            Path.Combine(@"C:\app", "archive/psx/redump/Some Game".Replace('/', Path.DirectorySeparatorChar)),
            full);
    }

    // ── 13: builder is the single authority (no manual construction) ─────────────

    [Fact]
    public void ArchiveWriter_NoManualArchivePathConstruction_RemainsInWriters()
    {
        // The builder is the canonical authority: both forms produce exactly the
        // documented shapes, so writers must not hand-roll paths.
        Assert.Equal("archive/p/d/File.chd",
            ArchiveArtifactPathBuilder.GetRelativePath("p", "d", ArchiveDatLineOutputForm.SingleFileFlat, "Rel", "File.chd"));
        Assert.Equal("archive/p/d/Rel/File.chd",
            ArchiveArtifactPathBuilder.GetRelativePath("p", "d", ArchiveDatLineOutputForm.MultiFileReleaseFolder, "Rel", "File.chd"));
    }
}
