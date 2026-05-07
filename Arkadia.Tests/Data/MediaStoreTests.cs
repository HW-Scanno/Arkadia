using System;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class MediaStoreTests : IDisposable
{
    private readonly string _dataDir;

    public MediaStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    // ── EnsureMediaFolders ────────────────────────────────────────────────────

    [Fact]
    public void EnsureMediaFolders_CreatesAllExpectedFlatFolders()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var root = Path.Combine(_dataDir, "media", "snes", "dat001");

        string[] expected =
        [
            "covers-front", "covers-back", "covers-spine", "covers-wrap",
            "screenshots-title", "screenshots",
            "fanart", "videos",
            "logos-hd", "logos",
            "manuals", "marquees", "flyers", "metadata",
        ];
        foreach (var folder in expected)
            Assert.True(Directory.Exists(Path.Combine(root, folder)), $"Missing folder: {folder}");
    }

    [Fact]
    public void EnsureMediaFolders_IsIdempotent()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var ex = Record.Exception(() => MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001"));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureMediaFolders_DoesNotCreateOldCoversSubdirTree()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var root = Path.Combine(_dataDir, "media", "snes", "dat001");
        Assert.False(Directory.Exists(Path.Combine(root, "covers", "front")));
        Assert.False(Directory.Exists(Path.Combine(root, "covers")));
    }

    // ── ReleaseStem ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Super Mario World",       "super_mario_world")]
    [InlineData("Castlevania: SoN",        "castlevania__son")]
    [InlineData("Game <Name> (USA)",       "game__name__(usa)")]
    [InlineData("already_lower",           "already_lower")]
    public void ReleaseStem_NormalizesCorrectly(string input, string expected)
    {
        Assert.Equal(expected, MediaStore.ReleaseStem(input));
    }

    // ── FindCoverFront — extension guard ──────────────────────────────────────

    [Fact]
    public void FindCoverFront_ReturnsNull_WhenOnlyPhpFileExists()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var dir = Path.Combine(_dataDir, "media", "snes", "dat001", "covers-front");
        File.WriteAllText(Path.Combine(dir, "super_mario_world_wor_001.php"), "garbage");

        Assert.Null(MediaStore.FindCoverFront(_dataDir, "snes", "dat001", "Super Mario World"));
    }

    [Fact]
    public void FindCoverFront_ReturnsPath_WhenValidJpgExists()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var dir  = Path.Combine(_dataDir, "media", "snes", "dat001", "covers-front");
        var file = Path.Combine(dir, "super_mario_world_wor_001.jpg");
        File.WriteAllBytes(file, [0xFF, 0xD8, 0xFF]);

        Assert.Equal(file, MediaStore.FindCoverFront(_dataDir, "snes", "dat001", "Super Mario World"));
    }

    [Fact]
    public void FindCoverFront_ReturnsNull_WhenDirectoryMissing()
    {
        Assert.Null(MediaStore.FindCoverFront(_dataDir, "snes", "nonexistent", "Any Game"));
    }

    [Fact]
    public void FindCoverFront_IgnoresNonImageExtensions()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "nes", "dat002");
        var dir = Path.Combine(_dataDir, "media", "nes", "dat002", "covers-front");
        File.WriteAllText(Path.Combine(dir, "metroid_wor_001.tmp"),  "not an image");
        File.WriteAllText(Path.Combine(dir, "metroid_wor_001.json"), "not an image");

        Assert.Null(MediaStore.FindCoverFront(_dataDir, "nes", "dat002", "Metroid"));
    }

    [Fact]
    public void FindCoverFront_AcceptsPng()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "gba", "dat003");
        var dir  = Path.Combine(_dataDir, "media", "gba", "dat003", "covers-front");
        var file = Path.Combine(dir, "castlevania_wor_001.png");
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47]);

        Assert.Equal(file, MediaStore.FindCoverFront(_dataDir, "gba", "dat003", "Castlevania"));
    }

    // ── FindCoverFront — region priority ──────────────────────────────────────

    [Fact]
    public void FindCoverFront_PrefersWorld_WhenMultipleRegionsPresent()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        File.WriteAllBytes(Path.Combine(dir, "game_eu_001.png"),  [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(dir, "game_wor_001.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(dir, "game_jp_001.png"),  [0x89, 0x50, 0x4E, 0x47]);

        var result = MediaStore.FindCoverFront(_dataDir, "snes", "d1", "game");

        Assert.NotNull(result);
        Assert.Contains("_wor_", result);
    }

    [Fact]
    public void FindCoverFront_FallsBackToUs_WhenNoWorld()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        File.WriteAllBytes(Path.Combine(dir, "game_eu_001.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(dir, "game_us_001.png"), [0x89, 0x50, 0x4E, 0x47]);

        var result = MediaStore.FindCoverFront(_dataDir, "snes", "d1", "game");

        Assert.NotNull(result);
        Assert.Contains("_us_", result);
    }

    [Fact]
    public void FindCoverFront_FallsBackToEu_WhenNoWorldOrUs()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        File.WriteAllBytes(Path.Combine(dir, "game_eu_001.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(dir, "game_jp_001.png"), [0x89, 0x50, 0x4E, 0x47]);

        var result = MediaStore.FindCoverFront(_dataDir, "snes", "d1", "game");

        Assert.NotNull(result);
        Assert.Contains("_eu_", result);
    }

    [Fact]
    public void FindCoverFront_FallsBackToJp_WhenOnlyJapanPresent()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir  = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        var file = Path.Combine(dir, "game_jp_001.png");
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47]);

        var result = MediaStore.FindCoverFront(_dataDir, "snes", "d1", "game");

        Assert.Equal(file, result);
    }

    [Fact]
    public void FindCoverFront_FallsBackToAny_ForUnknownRegion()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir  = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        var file = Path.Combine(dir, "game_kr_001.png");
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47]);

        var result = MediaStore.FindCoverFront(_dataDir, "snes", "d1", "game");

        Assert.Equal(file, result);
    }

    [Fact]
    public void FindCoverFront_FallsBackToLegacyFile_WithoutRegionEncoding()
    {
        // Files downloaded before region encoding was introduced (just <stem>_001)
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir  = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        var file = Path.Combine(dir, "game_001.png");
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47]);

        var result = MediaStore.FindCoverFront(_dataDir, "snes", "d1", "game");

        Assert.Equal(file, result);
    }

    // ── FindAllCoverRegions ───────────────────────────────────────────────────

    [Fact]
    public void FindAllCoverRegions_ReturnsAllRegions_WithCorrectParsing()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        File.WriteAllBytes(Path.Combine(dir, "game_wor_001.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(dir, "game_eu_001.png"),  [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(dir, "game_jp_001.png"),  [0x89, 0x50, 0x4E, 0x47]);

        var result = MediaStore.FindAllCoverRegions(_dataDir, "snes", "d1", "game", "covers-front");

        Assert.Equal(3, result.Count);
        Assert.Contains(result, x => x.Region == "wor");
        Assert.Contains(result, x => x.Region == "eu");
        Assert.Contains(result, x => x.Region == "jp");
    }

    [Fact]
    public void FindAllCoverRegions_ReturnsEmpty_WhenNoFiles()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var result = MediaStore.FindAllCoverRegions(_dataDir, "snes", "d1", "game", "covers-front");
        Assert.Empty(result);
    }

    [Fact]
    public void FindAllCoverRegions_ExcludesOtherStemFiles()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        File.WriteAllBytes(Path.Combine(dir, "game_wor_001.png"),  [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(dir, "other_wor_001.png"), [0x89, 0x50, 0x4E, 0x47]);

        var result = MediaStore.FindAllCoverRegions(_dataDir, "snes", "d1", "game", "covers-front");

        Assert.Single(result);
        Assert.Contains("game", result[0].Path);
    }

    [Fact]
    public void FindAllCoverRegions_MultipleFilesPerRegion()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        File.WriteAllBytes(Path.Combine(dir, "game_wor_001.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(dir, "game_wor_002.png"), [0x89, 0x50, 0x4E, 0x47]);

        var result = MediaStore.FindAllCoverRegions(_dataDir, "snes", "d1", "game", "covers-front");

        Assert.Equal(2, result.Count);
        Assert.All(result, x => Assert.Equal("wor", x.Region));
    }

    // ── FindTitleScreenshots / FindScreenshots / FindAllScreenshots ───────────

    [Fact]
    public void FindTitleScreenshots_ReturnsFromTitleFolder()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var titleDir    = Path.Combine(_dataDir, "media", "snes", "d1", "screenshots-title");
        var gameplayDir = Path.Combine(_dataDir, "media", "snes", "d1", "screenshots");
        File.WriteAllText(Path.Combine(titleDir,    "game_001.png"), "title");
        File.WriteAllText(Path.Combine(gameplayDir, "game_001.png"), "gameplay");

        var result = MediaStore.FindTitleScreenshots(_dataDir, "snes", "d1", "game");

        Assert.Single(result);
        Assert.Contains("screenshots-title", result[0]);
    }

    [Fact]
    public void FindScreenshots_ReturnsFromGameplayFolder()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var titleDir    = Path.Combine(_dataDir, "media", "snes", "d1", "screenshots-title");
        var gameplayDir = Path.Combine(_dataDir, "media", "snes", "d1", "screenshots");
        File.WriteAllText(Path.Combine(titleDir,    "game_001.png"), "title");
        File.WriteAllText(Path.Combine(gameplayDir, "game_001.png"), "gameplay");

        var result = MediaStore.FindScreenshots(_dataDir, "snes", "d1", "game");

        Assert.Single(result);
        Assert.DoesNotContain("screenshots-title", result[0]);
        Assert.Contains("screenshots", result[0]);
    }

    [Fact]
    public void FindAllScreenshots_ReturnsTitleBeforeGameplay()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var titleDir    = Path.Combine(_dataDir, "media", "snes", "d1", "screenshots-title");
        var gameplayDir = Path.Combine(_dataDir, "media", "snes", "d1", "screenshots");
        File.WriteAllText(Path.Combine(gameplayDir, "game_001.png"), "a");
        File.WriteAllText(Path.Combine(gameplayDir, "game_002.png"), "b");
        File.WriteAllText(Path.Combine(titleDir,    "game_001.png"), "t");

        var result = MediaStore.FindAllScreenshots(_dataDir, "snes", "d1", "game");

        Assert.Equal(3, result.Count);
        Assert.Contains("screenshots-title", result[0]);
        Assert.DoesNotContain("screenshots-title", result[1]);
        Assert.DoesNotContain("screenshots-title", result[2]);
    }

    [Fact]
    public void FindAllScreenshots_WhenOnlyTitle_ReturnsTitleOnly()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var titleDir = Path.Combine(_dataDir, "media", "snes", "d1", "screenshots-title");
        File.WriteAllText(Path.Combine(titleDir, "game_001.png"), "t");

        var result = MediaStore.FindAllScreenshots(_dataDir, "snes", "d1", "game");

        Assert.Single(result);
        Assert.Contains("screenshots-title", result[0]);
    }

    [Fact]
    public void FindAllScreenshots_WhenOnlyGameplay_ReturnsGameplayOnly()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var gameplayDir = Path.Combine(_dataDir, "media", "snes", "d1", "screenshots");
        File.WriteAllText(Path.Combine(gameplayDir, "game_001.png"), "a");

        var result = MediaStore.FindAllScreenshots(_dataDir, "snes", "d1", "game");

        Assert.Single(result);
        Assert.DoesNotContain("screenshots-title", result[0]);
    }

    [Fact]
    public void FindAllScreenshots_ReturnsEmpty_WhenNone()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        Assert.Empty(MediaStore.FindAllScreenshots(_dataDir, "snes", "d1", "game"));
    }

    // Legacy FindScreenshots tests (renamed to gameplay folder) ────────────────

    [Fact]
    public void FindScreenshots_EmptyDirectory_ReturnsEmpty()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        Assert.Empty(MediaStore.FindScreenshots(_dataDir, "snes", "dat001", "Super Mario World"));
    }

    [Fact]
    public void FindScreenshots_ReturnsSortedAlphabetically()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var dir = Path.Combine(_dataDir, "media", "snes", "dat001", "screenshots");
        File.WriteAllText(Path.Combine(dir, "super_mario_world_003.png"), "c");
        File.WriteAllText(Path.Combine(dir, "super_mario_world_001.png"), "a");
        File.WriteAllText(Path.Combine(dir, "super_mario_world_002.jpg"), "b");

        var result = MediaStore.FindScreenshots(_dataDir, "snes", "dat001", "Super Mario World");

        Assert.Equal(3, result.Count);
        Assert.EndsWith("_001.png", result[0]);
        Assert.EndsWith("_002.jpg", result[1]);
        Assert.EndsWith("_003.png", result[2]);
    }

    [Fact]
    public void FindScreenshots_ExcludesOtherReleaseStem()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var dir = Path.Combine(_dataDir, "media", "snes", "dat001", "screenshots");
        File.WriteAllText(Path.Combine(dir, "super_mario_world_001.png"), "x");
        File.WriteAllText(Path.Combine(dir, "other_game_001.png"),        "y");

        var result = MediaStore.FindScreenshots(_dataDir, "snes", "dat001", "Super Mario World");

        Assert.Single(result);
        Assert.Contains("super_mario_world", result[0]);
    }

    [Fact]
    public void FindScreenshots_MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(MediaStore.FindScreenshots(_dataDir, "snes", "nonexistent", "Any Game"));
    }

    [Fact]
    public void FindScreenshots_FirstIsLowestIndex()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "gba", "dat010");
        var dir = Path.Combine(_dataDir, "media", "gba", "dat010", "screenshots");
        File.WriteAllText(Path.Combine(dir, "metroid_002.png"), "b");
        File.WriteAllText(Path.Combine(dir, "metroid_001.jpg"), "a");

        var result = MediaStore.FindScreenshots(_dataDir, "gba", "dat010", "Metroid");

        Assert.Equal(2, result.Count);
        Assert.EndsWith("metroid_001.jpg", result[0]);
    }

    // ── FindLogos ─────────────────────────────────────────────────────────────

    [Fact]
    public void FindLogos_ReturnsHdBeforeStandard()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var hdDir  = Path.Combine(_dataDir, "media", "snes", "d1", "logos-hd");
        var stdDir = Path.Combine(_dataDir, "media", "snes", "d1", "logos");
        File.WriteAllText(Path.Combine(stdDir, "game_001.png"), "std");
        File.WriteAllText(Path.Combine(hdDir,  "game_001.png"), "hd");

        var result = MediaStore.FindLogos(_dataDir, "snes", "d1", "game");

        Assert.Equal(2, result.Count);
        Assert.Contains("logos-hd", result[0]);
        Assert.DoesNotContain("logos-hd", result[1]);
        Assert.Contains("logos",    result[1]);
    }

    [Fact]
    public void FindLogos_ReturnsStandardOnly_WhenNoHd()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var stdDir = Path.Combine(_dataDir, "media", "snes", "d1", "logos");
        File.WriteAllText(Path.Combine(stdDir, "game_001.png"), "std");

        var result = MediaStore.FindLogos(_dataDir, "snes", "d1", "game");

        Assert.Single(result);
        Assert.DoesNotContain("logos-hd", result[0]);
    }

    [Fact]
    public void FindLogos_ReturnsEmpty_WhenNone()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        Assert.Empty(MediaStore.FindLogos(_dataDir, "snes", "d1", "game"));
    }

    // ── FindFanart / FindMarquee / FindFlyer / FindManuals ────────────────────

    [Fact]
    public void FindFanart_ReturnsMultipleFiles_Sorted()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir = Path.Combine(_dataDir, "media", "snes", "d1", "fanart");
        File.WriteAllText(Path.Combine(dir, "game_002.jpg"), "b");
        File.WriteAllText(Path.Combine(dir, "game_001.png"), "a");

        var result = MediaStore.FindFanart(_dataDir, "snes", "d1", "game");

        Assert.Equal(2, result.Count);
        Assert.EndsWith("_001.png", result[0]);
        Assert.EndsWith("_002.jpg", result[1]);
    }

    [Fact]
    public void FindMarquee_ReturnsFirstFile()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir  = Path.Combine(_dataDir, "media", "snes", "d1", "marquees");
        var file = Path.Combine(dir, "game_001.png");
        File.WriteAllText(file, "m");

        Assert.Equal(file, MediaStore.FindMarquee(_dataDir, "snes", "d1", "game"));
    }

    [Fact]
    public void FindMarquee_ReturnsNull_WhenMissing()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        Assert.Null(MediaStore.FindMarquee(_dataDir, "snes", "d1", "game"));
    }

    [Fact]
    public void FindFlyer_ReturnsNull_WhenMissing()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        Assert.Null(MediaStore.FindFlyer(_dataDir, "snes", "d1", "game"));
    }

    [Fact]
    public void FindManuals_ReturnsMultipleFiles_Sorted()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir = Path.Combine(_dataDir, "media", "snes", "d1", "manuals");
        File.WriteAllText(Path.Combine(dir, "game_002.pdf"), "b");
        File.WriteAllText(Path.Combine(dir, "game_001.pdf"), "a");

        var result = MediaStore.FindManuals(_dataDir, "snes", "d1", "game");

        Assert.Equal(2, result.Count);
        Assert.EndsWith("_001.pdf", result[0]);
    }

    // ── NextIndexedMediaPath (non-cover) ──────────────────────────────────────

    [Fact]
    public void NextIndexedMediaPath_StartsAt001_WhenFolderEmpty()
    {
        var path = MediaStore.NextIndexedMediaPath(
            _dataDir, "snes", "dat001", "Super Mario World", "covers-front", "jpg");
        Assert.EndsWith("super_mario_world_001.jpg", path);
    }

    [Fact]
    public void NextIndexedMediaPath_IncrementsIndex_WhenFilesExist()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var dir = Path.Combine(_dataDir, "media", "snes", "dat001", "covers-front");
        File.WriteAllText(Path.Combine(dir, "super_mario_world_001.jpg"), "");

        var path = MediaStore.NextIndexedMediaPath(
            _dataDir, "snes", "dat001", "Super Mario World", "covers-front", "png");
        Assert.EndsWith("super_mario_world_002.png", path);
    }

    // ── NextIndexedCoverStem (regional) ───────────────────────────────────────

    [Fact]
    public void NextIndexedCoverStem_StartsAt001_WhenFolderEmpty()
    {
        var stem = MediaStore.NextIndexedCoverStem(
            _dataDir, "snes", "d1", "Game Name", "covers-front", "wor");
        Assert.EndsWith("game_name_wor_001", stem);
    }

    [Fact]
    public void NextIndexedCoverStem_IncrementsForSameRegion()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        File.WriteAllText(Path.Combine(dir, "game_wor_001.jpg"), "");
        File.WriteAllText(Path.Combine(dir, "game_wor_002.jpg"), "");

        var stem = MediaStore.NextIndexedCoverStem(
            _dataDir, "snes", "d1", "game", "covers-front", "wor");
        Assert.EndsWith("game_wor_003", stem);
    }

    [Fact]
    public void NextIndexedCoverStem_IndexesRegionsIndependently()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "d1");
        var dir = Path.Combine(_dataDir, "media", "snes", "d1", "covers-front");
        File.WriteAllText(Path.Combine(dir, "game_wor_001.jpg"), "");
        File.WriteAllText(Path.Combine(dir, "game_wor_002.jpg"), "");

        // eu should start at 001 regardless of how many wor files exist
        var euStem = MediaStore.NextIndexedCoverStem(
            _dataDir, "snes", "d1", "game", "covers-front", "eu");
        Assert.EndsWith("game_eu_001", euStem);
    }

    [Fact]
    public void NextIndexedCoverStem_CreatesDirectory_WhenMissing()
    {
        var stem = MediaStore.NextIndexedCoverStem(
            _dataDir, "md", "d2", "Sonic", "covers-front", "us");
        Assert.True(Directory.Exists(Path.GetDirectoryName(stem)));
        Assert.EndsWith("sonic_us_001", stem);
    }

    // ── NormalizeMediaType ────────────────────────────────────────────────────

    [Fact]
    public void NormalizeMediaType_PhysicalMedia_ReturnsPhysical()
        => Assert.Equal("physical", MediaStore.NormalizeMediaType("physical-media"));

    [Fact]
    public void NormalizeMediaType_Physical_ReturnsPhysical()
        => Assert.Equal("physical", MediaStore.NormalizeMediaType("physical"));

    [Fact]
    public void NormalizeMediaType_Null_ReturnsEmpty()
        => Assert.Equal("", MediaStore.NormalizeMediaType(null));

    [Fact]
    public void NormalizeMediaType_Empty_ReturnsEmpty()
        => Assert.Equal("", MediaStore.NormalizeMediaType(""));

    [Fact]
    public void NormalizeMediaType_Whitespace_ReturnsEmpty()
        => Assert.Equal("", MediaStore.NormalizeMediaType("   "));

    [Fact]
    public void NormalizeMediaType_CoverFront_Unchanged()
        => Assert.Equal("cover-front", MediaStore.NormalizeMediaType("cover-front"));

    [Fact]
    public void NormalizeMediaType_Video_Unchanged()
        => Assert.Equal("video", MediaStore.NormalizeMediaType("video"));

    [Fact]
    public void NormalizeMediaType_PhysicalTexture_Unchanged()
        => Assert.Equal("physical-texture", MediaStore.NormalizeMediaType("physical-texture"));
}
