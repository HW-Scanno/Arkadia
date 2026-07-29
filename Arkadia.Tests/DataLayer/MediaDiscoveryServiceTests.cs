using System;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Library;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class MediaDiscoveryServiceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly MediaDiscoveryService _svc;

    // Shared entry — all tests use the same hardware family / dat line / release name.
    private static LibraryEntry MakeEntry() => new()
    {
        Name             = "super_mario_world",
        HardwareFamilyId = "snes",
        DatLineId        = "dat001",
        ReleaseId        = "rel-001",
        Platform         = "SNES",
        Status           = "Present",
        Region           = "World",
        Languages        = "En",
        Format           = "ROM",
        Size             = "512 KB",
        Tier             = "1",
    };

    // Resolve the media root for a given subfolder.
    private string MediaDir(string folder) =>
        Path.Combine(_dataDir, "media", "snes", "dat001", folder);

    // Create a placeholder file so MediaStore.Find* picks it up.
    private string PlaceFile(string folder, string filename)
    {
        var dir  = MediaDir(folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, filename);
        File.WriteAllBytes(path, []);
        return path;
    }

    public MediaDiscoveryServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _svc = new MediaDiscoveryService(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    // ── FindGalleryItems ──────────────────────────────────────────────────────

    [Fact]
    public void FindGalleryItems_EmptyDir_ReturnsEmpty()
    {
        var result = _svc.FindGalleryItems(MakeEntry());
        Assert.Empty(result);
    }

    [Fact]
    public void FindGalleryItems_WithVideo_ReturnsVideoItem()
    {
        PlaceFile("videos", "super_mario_world_0.mp4");
        var result = _svc.FindGalleryItems(MakeEntry());
        Assert.Single(result);
        Assert.True(result[0].IsVideo);
        Assert.Equal("Video", result[0].Label);
    }

    [Fact]
    public void FindGalleryItems_WithTitleScreenshot_ReturnsTitleItem()
    {
        PlaceFile("screenshots-title", "super_mario_world_0.png");
        var result = _svc.FindGalleryItems(MakeEntry());
        Assert.Single(result);
        Assert.False(result[0].IsVideo);
        Assert.Equal("Title", result[0].Label);
    }

    [Fact]
    public void FindGalleryItems_WithGameplayScreenshot_ReturnsGameplayItem()
    {
        PlaceFile("screenshots", "super_mario_world_0.png");
        var result = _svc.FindGalleryItems(MakeEntry());
        Assert.Single(result);
        Assert.False(result[0].IsVideo);
        Assert.Equal("Gameplay", result[0].Label);
    }

    [Fact]
    public void FindGalleryItems_WithFanart_ReturnsFanartItem()
    {
        PlaceFile("fanart", "super_mario_world_0.png");
        var result = _svc.FindGalleryItems(MakeEntry());
        Assert.Single(result);
        Assert.False(result[0].IsVideo);
        Assert.Equal("Fanart", result[0].Label);
    }

    [Fact]
    public void FindGalleryItems_OrderIsVideoTitleGameplayFanart()
    {
        PlaceFile("fanart",             "super_mario_world_0.png");
        PlaceFile("screenshots",        "super_mario_world_0.png");
        PlaceFile("screenshots-title",  "super_mario_world_0.png");
        PlaceFile("videos",             "super_mario_world_0.mp4");

        var result = _svc.FindGalleryItems(MakeEntry());
        Assert.Equal(4, result.Count);
        Assert.Equal("Video",    result[0].Label);
        Assert.Equal("Title",    result[1].Label);
        Assert.Equal("Gameplay", result[2].Label);
        Assert.Equal("Fanart",   result[3].Label);
    }

    // ── FindCoverItems ────────────────────────────────────────────────────────

    [Fact]
    public void FindCoverItems_EmptyDir_ReturnsEmpty()
    {
        var result = _svc.FindCoverItems(MakeEntry());
        Assert.Empty(result);
    }

    [Fact]
    public void FindCoverItems_FrontOnly_ReturnsFrontLabel()
    {
        PlaceFile("covers-front", "super_mario_world_us_0.jpg");
        var result = _svc.FindCoverItems(MakeEntry());
        Assert.Single(result);
        Assert.Equal("Front", result[0].Label);
    }

    [Fact]
    public void FindCoverItems_FrontBackSpineWrap_ReturnsInCorrectOrder()
    {
        PlaceFile("covers-wrap",  "super_mario_world_us_0.jpg");
        PlaceFile("covers-spine", "super_mario_world_us_0.jpg");
        PlaceFile("covers-back",  "super_mario_world_us_0.jpg");
        PlaceFile("covers-front", "super_mario_world_us_0.jpg");

        var result = _svc.FindCoverItems(MakeEntry());
        Assert.Equal(4, result.Count);
        Assert.Equal("Front", result[0].Label);
        Assert.Equal("Back",  result[1].Label);
        Assert.Equal("Spine", result[2].Label);
        Assert.Equal("Wrap",  result[3].Label);
    }

    [Fact]
    public void FindCoverItems_MultipleRegions_ReturnsAll()
    {
        PlaceFile("covers-front", "super_mario_world_us_0.jpg");
        PlaceFile("covers-front", "super_mario_world_eu_0.jpg");
        var result = _svc.FindCoverItems(MakeEntry());
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Equal("Front", item.Label));
    }

    // ── FindExtrasItems ───────────────────────────────────────────────────────

    [Fact]
    public void FindExtrasItems_EmptyDir_ReturnsEmpty()
    {
        var result = _svc.FindExtrasItems(MakeEntry());
        Assert.Empty(result);
    }

    [Fact]
    public void FindExtrasItems_LogoFlyerMarquee_ReturnsInCorrectOrder()
    {
        PlaceFile("marquees", "super_mario_world_0.png");
        PlaceFile("flyers",   "super_mario_world_0.png");
        PlaceFile("logos",    "super_mario_world_0.png");

        var result = _svc.FindExtrasItems(MakeEntry());
        Assert.Equal(3, result.Count);
        Assert.Equal("Logo",    result[0].Label);
        Assert.Equal("Flyer",   result[1].Label);
        Assert.Equal("Marquee", result[2].Label);
    }

    [Fact]
    public void FindExtrasItems_LogoOnly_ReturnsOneLogoItem()
    {
        PlaceFile("logos", "super_mario_world_0.png");
        var result = _svc.FindExtrasItems(MakeEntry());
        Assert.Single(result);
        Assert.Equal("Logo", result[0].Label);
    }

    // ── FindManualPaths ───────────────────────────────────────────────────────

    [Fact]
    public void FindManualPaths_EmptyDir_ReturnsEmpty()
    {
        var result = _svc.FindManualPaths(MakeEntry());
        Assert.Empty(result);
    }

    [Fact]
    public void FindManualPaths_WithManuals_ReturnsPaths()
    {
        var p1 = PlaceFile("manuals", "super_mario_world_0.pdf");
        var p2 = PlaceFile("manuals", "super_mario_world_1.pdf");

        var result = _svc.FindManualPaths(MakeEntry());
        Assert.Equal(2, result.Count);
        Assert.Contains(p1, result);
        Assert.Contains(p2, result);
    }

    [Fact]
    public void FindManualPaths_ReturnsPathsForCorrectEntry()
    {
        PlaceFile("manuals", "super_mario_world_0.pdf");

        // A different entry in the same data dir should return empty.
        var other = new LibraryEntry
        {
            Name             = "zelda",
            HardwareFamilyId = "snes",
            DatLineId        = "dat001",
            ReleaseId        = "rel-002",
            Platform         = "SNES",
            Status           = "Present",
            Region           = "World",
            Languages        = "En",
            Format           = "ROM",
            Size             = "512 KB",
            Tier             = "1",
        };
        var result = _svc.FindManualPaths(other);
        Assert.Empty(result);
    }
}
