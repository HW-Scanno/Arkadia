using System;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class ReleaseMediaCurationServiceImportTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _dbPath;
    private const string HwFamilyId  = "snes";
    private const string DatLineId   = "snes-nointro";
    private const string ReleaseId   = "rel-001";
    private const string ReleaseName = "Super Mario World";

    public ReleaseMediaCurationServiceImportTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        var systemDir = Path.Combine(_baseDir, "data", "systems", HwFamilyId);
        Directory.CreateDirectory(systemDir);
        _dbPath = Path.Combine(systemDir, $"{DatLineId}.db");

        MediaStore.EnsureMediaFolders(Path.Combine(_baseDir, "data"), HwFamilyId, DatLineId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    private ReleaseMediaCurationService Svc() => new(Path.Combine(_baseDir, "data"));
    private DatLineStore Store() => new(_dbPath);

    private string PlaceIncoming(string filename, byte[]? content = null)
    {
        var dir  = Path.Combine(_baseDir, "incoming-media");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, filename);
        File.WriteAllBytes(path, content ?? [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x00, 0x0D]);
        return path;
    }

    // ── 1. Success result ────────────────────────────────────────────────────

    [Fact]
    public void Import_ReturnsSuccess_OnValidInput()
    {
        var src    = PlaceIncoming("cover.png");
        var result = Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "cover-front");
        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
    }

    // ── 2. File copied to destination ────────────────────────────────────────

    [Fact]
    public void Import_CopiesFileToDestination()
    {
        var src    = PlaceIncoming("cover.png");
        var result = Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "cover-front");
        Assert.NotNull(result.Asset);
        Assert.True(File.Exists(result.Asset!.FilePath));
    }

    // ── 3. Curation row created ──────────────────────────────────────────────

    [Fact]
    public void Import_RegistersCurationRow()
    {
        var src = PlaceIncoming("cover.png");
        Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "cover-front");
        var rows = Store().LoadMediaCurationRows(ReleaseId);
        Assert.Single(rows);
    }

    // ── 4. SHA-256 stored on curation row ────────────────────────────────────

    [Fact]
    public void Import_StoresCorrectSha256_OnCurationRow()
    {
        var src    = PlaceIncoming("cover.png");
        var result = Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "cover-front");

        var expected = ReleaseMediaCurationService.ComputeSha256(result.Asset!.FilePath);
        Assert.Equal(expected, result.Asset.Sha256);
    }

    // ── 5. Sets preferred when first of type ─────────────────────────────────

    [Fact]
    public void Import_SetsPreferred_WhenFirstOfType()
    {
        var src    = PlaceIncoming("cover.png");
        var result = Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "cover-front");
        Assert.True(result.Asset!.IsPreferred);
    }

    // ── 6. Does not set preferred when existing rows for type ────────────────

    [Fact]
    public void Import_DoesNotSetPreferred_WhenExistingRowsForType()
    {
        var src1 = PlaceIncoming("cover1.png");
        var src2 = PlaceIncoming("cover2.png");
        var svc  = Svc();

        svc.ImportFromIncoming(_dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId, src1, "cover-front");
        var result2 = svc.ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId, src2, "cover-front");

        Assert.False(result2.Asset!.IsPreferred);
    }

    // ── 7. Source not found ──────────────────────────────────────────────────

    [Fact]
    public void Import_ReturnsFailure_WhenSourceNotFound()
    {
        var result = Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            Path.Combine(_baseDir, "nonexistent.png"), "cover-front");
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.Asset);
    }

    // ── 8. Unknown media type ────────────────────────────────────────────────

    [Fact]
    public void Import_ReturnsFailure_WhenMediaTypeUnknown()
    {
        var src    = PlaceIncoming("cover.png");
        var result = Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "not-a-real-type");
        Assert.False(result.Success);
        Assert.Null(result.Asset);
    }

    // ── 9. Delete source after import ────────────────────────────────────────

    [Fact]
    public void Import_DeletesSource_WhenDeleteAfterSuccessIsTrue()
    {
        var src = PlaceIncoming("cover.png");
        Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "cover-front", deleteSourceAfterSuccess: true);
        Assert.False(File.Exists(src));
    }

    // ── 10. Preserve source when flag is false ───────────────────────────────

    [Fact]
    public void Import_PreservesSource_WhenDeleteAfterSuccessIsFalse()
    {
        var src = PlaceIncoming("cover.png");
        Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "cover-front", deleteSourceAfterSuccess: false);
        Assert.True(File.Exists(src));
    }

    // ── 11. Cover type goes to covers-front/ folder ──────────────────────────

    [Fact]
    public void Import_CoverType_DestinationInCoversFrontFolder()
    {
        var src    = PlaceIncoming("cover.png");
        var result = Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "cover-front");
        var expectedDir = Path.Combine(_baseDir, "data", "media", HwFamilyId, DatLineId, "covers-front");
        Assert.StartsWith(expectedDir, result.Asset!.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    // ── 12. Screenshot goes to screenshots/ folder ───────────────────────────

    [Fact]
    public void Import_NonCoverType_DestinationInScreenshotsFolder()
    {
        var src    = PlaceIncoming("screen.png");
        var result = Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "screenshot");
        var expectedDir = Path.Combine(_baseDir, "data", "media", HwFamilyId, DatLineId, "screenshots");
        Assert.StartsWith(expectedDir, result.Asset!.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    // ── 13. Result asset has correct properties on success ───────────────────

    [Fact]
    public void Import_ResultAsset_HasCorrectProperties()
    {
        var src    = PlaceIncoming("cover.png");
        var result = Svc().ImportFromIncoming(
            _dbPath, ReleaseId, ReleaseName, HwFamilyId, DatLineId,
            src, "cover-front");

        var asset = result.Asset!;
        Assert.Equal(ReleaseId,    asset.ReleaseId);
        Assert.Equal("cover-front", asset.MediaType);
        Assert.True(asset.Exists);
        Assert.False(asset.IsExcluded);
        Assert.NotNull(asset.Sha256);
        Assert.True(asset.SizeBytes > 0);
    }
}
