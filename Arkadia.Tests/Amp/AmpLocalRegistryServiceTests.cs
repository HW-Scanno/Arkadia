using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Amp;

public sealed class AmpLocalRegistryServiceTests : IDisposable
{
    private readonly string                  _dataDir;
    private readonly string                  _mediaDir;
    private readonly AmpLocalRegistryService _svc;
    private readonly AmpExportWriterService  _writer;

    public AmpLocalRegistryServiceTests()
    {
        _dataDir  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _mediaDir = Path.Combine(_dataDir, "media");
        Directory.CreateDirectory(_mediaDir);
        _svc    = new AmpLocalRegistryService(_dataDir);
        _writer = new AmpExportWriterService();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string PlaceMediaFile(string name)
    {
        var path = Path.Combine(_mediaDir, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    private AmpExportPlan CreatePlan(string mediaFilePath) => new(
        HardwareFamilyId:     "snes",
        DatLineId:            "snes-nointro",
        SystemName:           "Super Nintendo",
        ReleaseCount:         1,
        ReleasesWithMetadata: 1,
        ReleasesWithMedia:    1,
        TotalMediaFiles:      1,
        TotalBytes:           new FileInfo(mediaFilePath).Length,
        ExclusionCount:       0,
        ExtraNotesCount:      0,
        Releases: [new AmpExportPlanRelease(
            ReleaseId:       "rel-001",
            DatName:         "Super Mario World (USA)",
            Title:           "Super Mario World",
            OriginalTitle:   "",
            SortTitle:       "",
            Developer:       "Nintendo",
            Publisher:       "Nintendo",
            Year:            "1990",
            Languages:       "en",
            AlternateTitles: "",
            Description:     "",
            Genre:           "",
            Subgenre:        "",
            Players:         "",
            ReleaseType:     "",
            Rating:          "",
            HasMetadata:     true,
            MediaEntries: [new AmpExportPlanMediaEntry(
                MediaType:   "cover-front",
                FilePath:    mediaFilePath,
                Sha256:      ReleaseMediaCurationService.ComputeSha256(mediaFilePath)!,
                SizeBytes:   new FileInfo(mediaFilePath).Length,
                IsPreferred: true,
                Credits:     null)],
            ExclusionHashes: [],
            ExtraNotes:      null,
            Issues:          [])],
        Issues: []);

    private string CreateValidAmpInRegistry(string? baseName = null)
    {
        _svc.EnsureFolder();
        var mediaFile = PlaceMediaFile(Guid.NewGuid().ToString("N") + ".png");
        var plan      = CreatePlan(mediaFile);
        var outPath   = Path.Combine(
            _svc.RegistryFolder,
            (baseName ?? Guid.NewGuid().ToString("N")) + ".amp");
        _writer.Write(plan, outPath);
        return outPath;
    }

    private string CreateUnreadableAmpInRegistry()
    {
        _svc.EnsureFolder();
        var path = Path.Combine(_svc.RegistryFolder, Guid.NewGuid().ToString("N") + ".amp");
        File.WriteAllBytes(path, [0x00, 0x11, 0x22, 0x33]);
        return path;
    }

    private string CreateValidAmpOutsideRegistry(string? baseName = null)
    {
        var outDir = Path.Combine(_dataDir, "external");
        Directory.CreateDirectory(outDir);
        var mediaFile = PlaceMediaFile(Guid.NewGuid().ToString("N") + ".png");
        var plan      = CreatePlan(mediaFile);
        var outPath   = Path.Combine(outDir, (baseName ?? Guid.NewGuid().ToString("N")) + ".amp");
        _writer.Write(plan, outPath);
        return outPath;
    }

    private string CreateUnreadableAmpOutsideRegistry()
    {
        var outDir = Path.Combine(_dataDir, "external");
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, Guid.NewGuid().ToString("N") + ".amp");
        File.WriteAllBytes(path, [0x00, 0x11, 0x22, 0x33]);
        return path;
    }

    // ── EnsureFolder ──────────────────────────────────────────────────────────

    [Fact]
    public void EnsureFolder_CreatesRegistryFolder()
    {
        Assert.False(Directory.Exists(_svc.RegistryFolder));
        _svc.EnsureFolder();
        Assert.True(Directory.Exists(_svc.RegistryFolder));
    }

    // ── ListPackages ──────────────────────────────────────────────────────────

    [Fact]
    public void ListPackages_EmptyFolder_ReturnsEmpty()
    {
        var result = _svc.ListPackages();
        Assert.Empty(result);
    }

    [Fact]
    public void ListPackages_IgnoresNonAmpFiles()
    {
        _svc.EnsureFolder();
        File.WriteAllText(Path.Combine(_svc.RegistryFolder, "readme.txt"), "hello");
        File.WriteAllBytes(Path.Combine(_svc.RegistryFolder, "package.zip"), [0x50, 0x4B]);

        var result = _svc.ListPackages();
        Assert.Empty(result);
    }

    [Fact]
    public void ListPackages_ValidAmp_ReturnsPackageInfo()
    {
        var path   = CreateValidAmpInRegistry();
        var result = _svc.ListPackages();

        Assert.Single(result);
        Assert.Equal(path,                    result[0].FilePath);
        Assert.Equal(Path.GetFileName(path),  result[0].FileName);
        Assert.True(result[0].PackageBytes > 0);
    }

    [Fact]
    public void ListPackages_ReadsManifestFields()
    {
        CreateValidAmpInRegistry();
        var result = _svc.ListPackages();

        Assert.Single(result);
        var pkg = result[0];
        Assert.Equal("snes",           pkg.HardwareFamilyId);
        Assert.Equal("snes-nointro",   pkg.DatLineId);
        Assert.Equal("Super Nintendo", pkg.SystemName);
        Assert.Equal(1,                pkg.ReleaseCount);
        Assert.Equal(1,                pkg.MediaFileCount);
        Assert.Equal("Arkadia Media Pack", pkg.FormatName);
        Assert.Equal("1",              pkg.FormatVersion);
    }

    [Fact]
    public void ListPackages_ComputesPackageSha256()
    {
        var path     = CreateValidAmpInRegistry();
        var expected = ReleaseMediaCurationService.ComputeSha256(path)!;

        var result = _svc.ListPackages();

        Assert.Single(result);
        Assert.Equal(expected, result[0].PackageSha256);
        Assert.NotEmpty(result[0].PackageSha256);
    }

    [Fact]
    public void ListPackages_UnreadableAmp_ReturnsUnreadableStatus()
    {
        CreateUnreadableAmpInRegistry();
        var result = _svc.ListPackages();

        Assert.Single(result);
        Assert.Equal("Unreadable", result[0].Status);
        Assert.True(result[0].HasErrors);
        Assert.False(result[0].HasWarnings);
        Assert.Null(result[0].VerificationResult);
    }

    [Fact]
    public void ListPackages_DoesNotCallVerifier()
    {
        CreateValidAmpInRegistry();
        var result = _svc.ListPackages();

        Assert.Single(result);
        Assert.Equal("Unverified", result[0].Status);
        Assert.False(result[0].HasErrors);
        Assert.False(result[0].HasWarnings);
        Assert.Null(result[0].VerificationResult);
    }

    [Fact]
    public void ListPackages_DoesNotModifyPackage()
    {
        var path        = CreateValidAmpInRegistry();
        var fi          = new FileInfo(path);
        var lenBefore   = fi.Length;
        var mtimeBefore = fi.LastWriteTimeUtc;

        _svc.ListPackages();

        fi.Refresh();
        Assert.Equal(lenBefore,   fi.Length);
        Assert.Equal(mtimeBefore, fi.LastWriteTimeUtc);
    }

    // ── VerifyPackage ─────────────────────────────────────────────────────────

    [Fact]
    public void VerifyPackage_ValidAmp_ReturnsValidResult()
    {
        var path = CreateValidAmpInRegistry();
        var info = _svc.VerifyPackage(path);

        Assert.Equal("Valid", info.Status);
        Assert.False(info.HasErrors);
        Assert.False(info.HasWarnings);
        Assert.NotNull(info.VerificationResult);
        Assert.Equal(path, info.FilePath);
    }

    [Fact]
    public void VerifyPackage_InvalidAmp_ReturnsErrorResult()
    {
        var path = CreateUnreadableAmpInRegistry();
        var info = _svc.VerifyPackage(path);

        Assert.Equal("Error", info.Status);
        Assert.True(info.HasErrors);
        Assert.NotNull(info.VerificationResult);
    }

    [Fact]
    public void VerifyPackage_UsesManifestFieldsWhenAvailable()
    {
        var path = CreateValidAmpInRegistry();
        var info = _svc.VerifyPackage(path);

        Assert.Equal("snes",           info.HardwareFamilyId);
        Assert.Equal("snes-nointro",   info.DatLineId);
        Assert.Equal("Super Nintendo", info.SystemName);
        Assert.Equal(1,                info.ReleaseCount);
        Assert.NotNull(info.VerificationResult);
        Assert.NotEmpty(info.PackageSha256);
    }

    // ── RegisterLocalPackage ──────────────────────────────────────────────────

    [Fact]
    public void RegisterLocalPackage_ValidAmp_CopiesIntoRegistry()
    {
        var src     = CreateValidAmpOutsideRegistry();
        var dstPath = Path.Combine(_svc.RegistryFolder, Path.GetFileName(src));

        _svc.RegisterLocalPackage(src);

        Assert.True(File.Exists(dstPath));
    }

    [Fact]
    public void RegisterLocalPackage_ValidAmp_ReturnsVerifiedInfo()
    {
        var src  = CreateValidAmpOutsideRegistry();
        var info = _svc.RegisterLocalPackage(src);

        Assert.NotNull(info);
        Assert.NotNull(info.VerificationResult);
        Assert.False(info.HasErrors);
        Assert.Equal(Path.GetFileName(src), info.FileName);
    }

    [Fact]
    public void RegisterLocalPackage_ValidAmp_SourceFilePreserved()
    {
        var src    = CreateValidAmpOutsideRegistry();
        var lenBefore = new FileInfo(src).Length;

        _svc.RegisterLocalPackage(src);

        Assert.True(File.Exists(src));
        Assert.Equal(lenBefore, new FileInfo(src).Length);
    }

    [Fact]
    public void RegisterLocalPackage_Duplicate_Throws()
    {
        var src = CreateValidAmpOutsideRegistry("duplicate-test");
        _svc.RegisterLocalPackage(src);

        Assert.Throws<InvalidOperationException>(() =>
            _svc.RegisterLocalPackage(src));
    }

    [Fact]
    public void RegisterLocalPackage_Overwrite_ReplacesExisting()
    {
        var src  = CreateValidAmpOutsideRegistry("overwrite-test");
        var dst  = Path.Combine(_svc.RegistryFolder, Path.GetFileName(src));

        _svc.RegisterLocalPackage(src);
        var lenFirst = new FileInfo(dst).Length;

        var info = _svc.RegisterLocalPackage(src, overwrite: true);

        Assert.True(File.Exists(dst));
        Assert.Equal(lenFirst, new FileInfo(dst).Length);
        Assert.NotNull(info.VerificationResult);
    }

    [Fact]
    public void RegisterLocalPackage_NullPath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _svc.RegisterLocalPackage(null!));
    }

    [Fact]
    public void RegisterLocalPackage_WrongExtension_Throws()
    {
        var path = Path.Combine(_dataDir, "external", "package.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x50, 0x4B]);

        Assert.Throws<ArgumentException>(() =>
            _svc.RegisterLocalPackage(path));
    }

    [Fact]
    public void RegisterLocalPackage_MissingSource_Throws()
    {
        var path = Path.Combine(_dataDir, "external", "nonexistent.amp");

        Assert.Throws<FileNotFoundException>(() =>
            _svc.RegisterLocalPackage(path));
    }

    [Fact]
    public void RegisterLocalPackage_ErrorPackage_Throws()
    {
        var src = CreateUnreadableAmpOutsideRegistry();

        Assert.Throws<InvalidOperationException>(() =>
            _svc.RegisterLocalPackage(src));

        var dst = Path.Combine(_svc.RegistryFolder, Path.GetFileName(src));
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void RegisterLocalPackage_NoTmpFileLeftOnFailure()
    {
        var src     = CreateValidAmpOutsideRegistry("cancel-test");
        var dstPath = Path.Combine(_svc.RegistryFolder, Path.GetFileName(src));
        var tmpPath = dstPath + ".tmp";

        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _svc.RegisterLocalPackage(src, ct: cts.Token));

        Assert.False(File.Exists(tmpPath));
        Assert.False(File.Exists(dstPath));
    }
}
