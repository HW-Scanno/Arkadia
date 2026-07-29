using System;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class ReleaseMediaCurationServiceTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _dbPath;
    private const string HwFamilyId = "snes";
    private const string DatLineId  = "snes-nointro";
    private const string ReleaseId  = "rel-001";
    private const string ReleaseName = "Super Mario World";

    public ReleaseMediaCurationServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        var systemDir = Path.Combine(_baseDir, "data", "systems", HwFamilyId);
        Directory.CreateDirectory(systemDir);
        _dbPath = Path.Combine(systemDir, $"{DatLineId}.db");

        // Pre-create media folders
        var dataDir = Path.Combine(_baseDir, "data");
        Directory.CreateDirectory(dataDir);
        MediaStore.EnsureMediaFolders(dataDir, HwFamilyId, DatLineId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    private ReleaseMediaCurationService Svc() => new(Path.Combine(_baseDir, "data"));

    private DatLineStore Store() => new(_dbPath);

    private string MediaDir(string folder) =>
        Path.Combine(_baseDir, "data", "media", HwFamilyId, DatLineId, folder);

    private string PlaceFile(string folder, string filename)
    {
        var dir  = MediaDir(folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, filename);
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG header stub
        return path;
    }

    private string ReleaseStem => MediaStore.ReleaseStem(ReleaseName) + "_";

    // ── 1. LoadAssets returns existing media files ────────────────────────────

    [Fact]
    public void LoadAssets_ReturnsFrontCover()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        var svc  = Svc();

        var assets = svc.LoadAssets(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId);

        Assert.Contains(assets, a => a.FilePath == path && a.MediaType == "cover-front");
    }

    [Fact]
    public void LoadAssets_ReturnsMultipleMediaTypes()
    {
        PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        PlaceFile("screenshots",  ReleaseStem + "001.png");
        PlaceFile("videos",       ReleaseStem + "001.mp4");

        var assets = Svc().LoadAssets(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId);

        Assert.Contains(assets, a => a.MediaType == "cover-front");
        Assert.Contains(assets, a => a.MediaType == "screenshot");
        Assert.Contains(assets, a => a.MediaType == "video");
    }

    [Fact]
    public void LoadAssets_ExistsTrue_ForFilePresentOnDisk()
    {
        PlaceFile("covers-front", ReleaseStem + "wor_001.png");

        var assets = Svc().LoadAssets(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId);

        Assert.All(assets, a => Assert.True(a.Exists));
    }

    // ── 2. Exclude creates curation row with is_excluded=1 ───────────────────

    [Fact]
    public void Exclude_CreatesRowWithIsExcludedTrue()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().Exclude(_dbPath, ReleaseId, "cover-front", path, reason: null);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        Assert.Contains(rows, r => r.FilePath == path && r.IsExcluded);
    }

    // ── 3. Exclude computes SHA-256 ───────────────────────────────────────────

    [Fact]
    public void Exclude_ComputesAndStoresSha256()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().Exclude(_dbPath, ReleaseId, "cover-front", path, reason: null);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row  = Assert.Single(rows, r => r.FilePath == path);
        Assert.NotNull(row.FileSha256);
        Assert.Equal(64, row.FileSha256!.Length); // hex-encoded SHA-256
    }

    // ── 4. Restore clears is_excluded ────────────────────────────────────────

    [Fact]
    public void Restore_ClearsIsExcluded()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        var svc  = Svc();
        svc.Exclude(_dbPath, ReleaseId, "cover-front", path, reason: null);
        svc.Restore(_dbPath, ReleaseId, "cover-front", path);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row  = Assert.Single(rows, r => r.FilePath == path);
        Assert.False(row.IsExcluded);
    }

    // ── 5. SetPreferred marks selected asset preferred ────────────────────────

    [Fact]
    public void SetPreferred_MarksAssetAsPreferred()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().SetPreferred(_dbPath, ReleaseId, "cover-front", path);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row  = Assert.Single(rows, r => r.FilePath == path);
        Assert.True(row.IsPreferred);
    }

    // ── 6. SetPreferred clears previous preferred for same media type ─────────

    [Fact]
    public void SetPreferred_ClearsPreviousPreferred_SameType()
    {
        var path1 = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        var path2 = PlaceFile("covers-front", ReleaseStem + "wor_002.png");
        var svc   = Svc();

        svc.SetPreferred(_dbPath, ReleaseId, "cover-front", path1);
        svc.SetPreferred(_dbPath, ReleaseId, "cover-front", path2);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row1 = Assert.Single(rows, r => r.FilePath == path1);
        var row2 = Assert.Single(rows, r => r.FilePath == path2);
        Assert.False(row1.IsPreferred);
        Assert.True(row2.IsPreferred);
    }

    // ── 7. SaveCredits stores multiline credits ───────────────────────────────

    [Fact]
    public void SaveCredits_StoresMultilineCredits()
    {
        var path    = PlaceFile("logos", ReleaseStem + "001.png");
        var credits = "Artwork by Jane\nEdited by Joe";
        Svc().SaveCredits(_dbPath, ReleaseId, "logo", path, credits);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row  = Assert.Single(rows, r => r.FilePath == path);
        Assert.Equal(credits, row.Credits);
    }

    // ── 8. DeleteMediaFile removes curation row (B + C + D) ──────────────────

    [Fact]
    public void DeleteMediaFile_RemovesCurationRow()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().Exclude(_dbPath, ReleaseId, "cover-front", path, reason: null);
        Svc().DeleteMediaFile(_dbPath, ReleaseId, "cover-front", path);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        Assert.Empty(rows);
    }

    [Fact]
    public void DeleteMediaFile_DoesNotCreateExclusionRow()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().DeleteMediaFile(_dbPath, ReleaseId, "cover-front", path);

        // No row should exist at all — not excluded, not missing
        var rows = Store().LoadMediaCurationRows(ReleaseId);
        Assert.Empty(rows);
    }

    [Fact]
    public void DeleteMediaFile_DeletedAsset_DisappearsFromLoadAssets()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().Exclude(_dbPath, ReleaseId, "cover-front", path, reason: null);
        Svc().DeleteMediaFile(_dbPath, ReleaseId, "cover-front", path);

        var assets = Svc().LoadAssets(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId);
        Assert.DoesNotContain(assets, a => a.FilePath == path);
    }

    [Fact]
    public void DeleteMediaFile_OnExcludedAsset_RemovesExclusionRow()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().Exclude(_dbPath, ReleaseId, "cover-front", path, reason: "unwanted");

        var before = Store().LoadMediaCurationRows(ReleaseId);
        Assert.Contains(before, r => r.IsExcluded);

        Svc().DeleteMediaFile(_dbPath, ReleaseId, "cover-front", path);

        var after = Store().LoadMediaCurationRows(ReleaseId);
        Assert.Empty(after);
    }

    [Fact]
    public void DeleteMediaFile_FileDeleteFailure_RowPreserved()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Store().UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId,
            MediaType:      "cover-front",
            FilePath:       path,
            FileSha256:     "aabbcc",
            IsPreferred:    false,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() =>
                Svc().DeleteMediaFile(_dbPath, ReleaseId, "cover-front", path));

            var rows = Store().LoadMediaCurationRows(ReleaseId);
            Assert.Contains(rows, r => r.FilePath == path);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // ── 9. DeleteMediaFile removes file from disk ─────────────────────────────

    [Fact]
    public void DeleteMediaFile_RemovesFileFromDisk()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().DeleteMediaFile(_dbPath, ReleaseId, "cover-front", path);

        Assert.False(File.Exists(path));
    }

    // ── 10. DeleteMediaFile refuses unsafe path ───────────────────────────────

    [Fact]
    public void DeleteMediaFile_RefusesPathOutsideMediaRoot()
    {
        var outsidePath = Path.Combine(_baseDir, "some-other-file.png");
        File.WriteAllText(outsidePath, "data");

        Assert.Throws<ArgumentException>(() =>
            Svc().DeleteMediaFile(_dbPath, ReleaseId, "cover-front", outsidePath));
    }

    // ── 11. Missing file curation row remains visible as Exists=false ─────────

    [Fact]
    public void LoadAssets_MissingFileCurationRow_HasExistsFalse()
    {
        var fakePath = Path.Combine(_baseDir, "data", "media", HwFamilyId, DatLineId,
                                    "covers-front", ReleaseStem + "wor_deleted.png");

        Store().UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId,
            MediaType:      "cover-front",
            FilePath:       fakePath,
            FileSha256:     "aabbcc",
            IsPreferred:    false,
            IsExcluded:     true,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        var assets = Svc().LoadAssets(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId);

        Assert.Contains(assets, a => a.FilePath == fakePath && !a.Exists && a.IsExcluded);
    }

    // ── 12. Excluded media is not counted as Active by StatusLabel ────────────

    [Fact]
    public void ExcludedAsset_HasStatusLabel_Excluded()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().Exclude(_dbPath, ReleaseId, "cover-front", path, reason: null);

        var assets = Svc().LoadAssets(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId);
        var asset  = Assert.Single(assets, a => a.FilePath == path);
        Assert.Equal("Excluded", asset.StatusLabel);
    }

    // ── H. Exclude still creates exclusion row with SHA-256 ──────────────────

    [Fact]
    public void Exclude_CreatesExclusionRow_WithSha256()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().Exclude(_dbPath, ReleaseId, "cover-front", path, reason: null);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row  = Assert.Single(rows, r => r.FilePath == path);
        Assert.True(row.IsExcluded);
        Assert.NotNull(row.FileSha256);
    }

    [Fact]
    public void Exclude_RowPersistedAfterFileDeleted_FromDisk()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        Svc().Exclude(_dbPath, ReleaseId, "cover-front", path, reason: null);
        File.Delete(path);

        var assets = Svc().LoadAssets(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId);
        Assert.Contains(assets, a => a.FilePath == path && !a.Exists && a.IsExcluded);
    }

    // ── D. DeleteMediaFile on missing file removes curation row ──────────────

    [Fact]
    public void DeleteMediaFile_MissingAsset_RemovesCurationRow()
    {
        var fakePath = Path.Combine(_baseDir, "data", "media", HwFamilyId, DatLineId,
                                    "covers-front", ReleaseStem + "wor_gone.png");

        Store().UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId,
            MediaType:      "cover-front",
            FilePath:       fakePath,
            FileSha256:     null,
            IsPreferred:    false,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        Svc().DeleteMediaFile(_dbPath, ReleaseId, "cover-front", fakePath);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        Assert.DoesNotContain(rows, r => r.FilePath == fakePath);
    }

    // ── E. DeleteMediaFile on missing excluded asset removes exclusion row ────

    [Fact]
    public void DeleteMediaFile_MissingExcludedAsset_RemovesExclusionRow()
    {
        var fakePath = Path.Combine(_baseDir, "data", "media", HwFamilyId, DatLineId,
                                    "covers-front", ReleaseStem + "wor_gone.png");

        Store().UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId,
            MediaType:      "cover-front",
            FilePath:       fakePath,
            FileSha256:     "aabbcc1122",
            IsPreferred:    false,
            IsExcluded:     true,
            ExcludedReason: "never existed",
            Credits:        null,
            Notes:          null));

        var before = Store().LoadMediaCurationRows(ReleaseId);
        Assert.Contains(before, r => r.FilePath == fakePath && r.IsExcluded);

        Svc().DeleteMediaFile(_dbPath, ReleaseId, "cover-front", fakePath);

        var after = Store().LoadMediaCurationRows(ReleaseId);
        Assert.DoesNotContain(after, r => r.FilePath == fakePath);
    }

    // ── 13. Unique constraint prevents duplicate curation rows ────────────────

    [Fact]
    public void UpsertCurationRow_IsIdempotent_NoException()
    {
        var path  = PlaceFile("logos", ReleaseStem + "001.png");
        var store = Store();
        var row   = new MediaCurationRow(ReleaseId, "logo", path,
                        null, false, false, null, null, null);

        store.UpsertMediaCurationRow(row);
        store.UpsertMediaCurationRow(row); // should not throw

        var rows = store.LoadMediaCurationRows(ReleaseId);
        Assert.Equal(1, rows.Count(r => r.FilePath == path));
    }

    // ── 14. ReleaseMediaAsset exposes no provider/source provenance ───────────

    [Fact]
    public void ReleaseMediaAsset_HasNoProviderField()
    {
        var type   = typeof(ReleaseMediaAsset);
        var props  = type.GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain(props, p => p.Contains("provider"));
        Assert.DoesNotContain(props, p => p.Contains("source"));
        Assert.DoesNotContain(props, p => p.Contains("scraper"));
        Assert.DoesNotContain(props, p => p.Contains("url"));
    }

    // ── 15. Credits are stored separately from provenance ────────────────────

    [Fact]
    public void SaveCredits_DoesNotAffectSha256OrExcludedState()
    {
        var path = PlaceFile("screenshots", ReleaseStem + "001.png");
        var svc  = Svc();

        svc.Exclude(_dbPath, ReleaseId, "screenshot", path, reason: null);
        svc.SaveCredits(_dbPath, ReleaseId, "screenshot", path, "Photo by John");

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row  = Assert.Single(rows, r => r.FilePath == path);

        Assert.True(row.IsExcluded);
        Assert.NotNull(row.FileSha256);
        Assert.Equal("Photo by John", row.Credits);
    }

    // ── 16–18. physical-media alias normalizes to canonical "physical" ─────────

    [Fact]
    public void Exclude_WithPhysicalMediaAlias_StoresAsCanonicalPhysical()
    {
        var path = PlaceFile("physical", ReleaseStem + "001.png");

        // Pass the provider alias "physical-media" — must be stored as "physical".
        Svc().Exclude(_dbPath, ReleaseId, "physical-media", path, reason: "unwanted");

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row  = Assert.Single(rows, r => r.FilePath == path);
        Assert.Equal("physical", row.MediaType);
        Assert.True(row.IsExcluded);
    }

    [Fact]
    public void SetPreferred_WithPhysicalMediaAlias_StoresAsCanonicalPhysical()
    {
        var path = PlaceFile("physical", ReleaseStem + "001.png");

        // Pass the provider alias "physical-media" — must be stored as "physical".
        Svc().SetPreferred(_dbPath, ReleaseId, "physical-media", path);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row  = Assert.Single(rows, r => r.FilePath == path);
        Assert.Equal("physical", row.MediaType);
        Assert.True(row.IsPreferred);
    }

    // ── 19. SetPreferred preserves credits on existing row ────────────────────

    [Fact]
    public void SetPreferred_PreservesCredits_OnExistingRow()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        var svc  = Svc();

        // Seed a curation row with credits.
        svc.SaveCredits(_dbPath, ReleaseId, "cover-front", path, "Scan by Jane");

        // SetPreferred should not clear the credits.
        svc.SetPreferred(_dbPath, ReleaseId, "cover-front", path);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row  = Assert.Single(rows, r => r.FilePath == path);
        Assert.True(row.IsPreferred);
        Assert.Equal("Scan by Jane", row.Credits);
    }

    // ── 20. SetPreferred preserves excluded state on existing row ─────────────

    [Fact]
    public void SetPreferred_PreservesExcludedState_OnExistingRow()
    {
        var path = PlaceFile("covers-front", ReleaseStem + "wor_001.png");
        var svc  = Svc();

        // Exclude the asset first.
        svc.Exclude(_dbPath, ReleaseId, "cover-front", path, reason: "test");

        // SetPreferred on an excluded asset should keep is_excluded intact.
        svc.SetPreferred(_dbPath, ReleaseId, "cover-front", path);

        var rows = Store().LoadMediaCurationRows(ReleaseId);
        var row  = Assert.Single(rows, r => r.FilePath == path);
        Assert.True(row.IsPreferred);
        Assert.True(row.IsExcluded);
    }

    // ── 21. AddMediaFile — successive adds go to different paths ──────────────

    [Fact]
    public void AddMediaFile_TwoAdds_CreateDistinctFiles()
    {
        var source = Path.Combine(_baseDir, "source.png");
        File.WriteAllBytes(source, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var svc    = Svc();
        var asset1 = svc.AddMediaFile(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId, source, "cover-front");
        var asset2 = svc.AddMediaFile(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId, source, "cover-front");

        Assert.NotEqual(asset1.FilePath, asset2.FilePath);
        Assert.True(File.Exists(asset1.FilePath));
        Assert.True(File.Exists(asset2.FilePath));
    }

    // ── 22. AddMediaFile — cleans up copied file when DB write fails ──────────

    [Fact]
    public void AddMediaFile_CleansUpCopiedFile_WhenCurationWriteFails()
    {
        var source = Path.Combine(_baseDir, "source.png");
        File.WriteAllBytes(source, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        // A non-SQLite file forces DatLineStore to fail during EnsureSchema.
        var badDbPath = Path.Combine(_baseDir, "corrupt.db");
        File.WriteAllText(badDbPath, "NOT A VALID SQLITE DATABASE");

        Assert.ThrowsAny<Exception>(() =>
            Svc().AddMediaFile(badDbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId, source, "cover-front"));

        // The freshly copied file must have been deleted by the catch block.
        var coverDir = MediaDir("covers-front");
        var leftover = Directory.Exists(coverDir)
            ? Directory.GetFiles(coverDir)
            : [];
        Assert.Empty(leftover);
    }

    // ── 23. AddMediaFile — excluded rows prevent auto-set preferred ──────────

    [Fact]
    public void AddMediaFile_AllExcludedRows_DoesNotAutoSetPreferred()
    {
        var source = Path.Combine(_baseDir, "source.png");
        File.WriteAllBytes(source, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var svc = Svc();

        // Seed an excluded curation row for cover-front — counts as existing.
        Store().UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId,
            MediaType:      "cover-front",
            FilePath:       "/old/excluded.png",
            FileSha256:     "aabbccdd",
            IsPreferred:    false,
            IsExcluded:     true,
            ExcludedReason: "unwanted",
            Credits:        null,
            Notes:          null));

        var asset = svc.AddMediaFile(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId, source, "cover-front");

        // Excluded rows count toward the existing-rows check, so IsPreferred must be false.
        Assert.False(asset.IsPreferred);
    }

    // ── 18. (physical-media alias, cont.) ─────────────────────────────────────

    [Fact]
    public void LoadAssets_OldPhysicalMediaRow_NormalizedInMissingFileDisplay()
    {
        // Directly insert a legacy row with media_type = "physical-media"
        var fakePath = Path.Combine(_baseDir, "data", "media", HwFamilyId, DatLineId,
                                    "physical", ReleaseStem + "deleted.png");
        Store().UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId,
            MediaType:      "physical-media",   // old alias — written directly, bypassing normalization
            FilePath:       fakePath,
            FileSha256:     "aabb1234",
            IsPreferred:    false,
            IsExcluded:     true,
            ExcludedReason: "removed",
            Credits:        null,
            Notes:          null));

        // LoadAssets should normalize the media type in the missing-file display path.
        var assets = Svc().LoadAssets(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId);

        var asset = Assert.Single(assets, a => a.FilePath == fakePath);
        Assert.Equal("physical", asset.MediaType);   // normalized, never "physical-media"
        Assert.False(asset.Exists);
        Assert.True(asset.IsExcluded);
    }
}
