using System;
using System.IO;
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

    // ── FindCoverFront extension guard ────────────────────────────────────────

    [Fact]
    public void FindCoverFront_ReturnsNull_WhenOnlyPhpFileExists()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var dir = Path.Combine(_dataDir, "media", "snes", "dat001", "covers", "front");
        File.WriteAllText(Path.Combine(dir, "super_mario_world_001.php"), "garbage");

        var result = MediaStore.FindCoverFront(_dataDir, "snes", "dat001", "Super Mario World");

        Assert.Null(result);
    }

    [Fact]
    public void FindCoverFront_ReturnsPath_WhenValidJpgExists()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var dir  = Path.Combine(_dataDir, "media", "snes", "dat001", "covers", "front");
        var file = Path.Combine(dir, "super_mario_world_001.jpg");
        File.WriteAllBytes(file, [0xFF, 0xD8, 0xFF]);

        var result = MediaStore.FindCoverFront(_dataDir, "snes", "dat001", "Super Mario World");

        Assert.Equal(file, result);
    }

    [Fact]
    public void FindCoverFront_ReturnsNull_WhenDirectoryMissing()
    {
        var result = MediaStore.FindCoverFront(_dataDir, "snes", "nonexistent", "Any Game");
        Assert.Null(result);
    }

    [Fact]
    public void FindCoverFront_IgnoresNonImageExtensions()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "nes", "dat002");
        var dir = Path.Combine(_dataDir, "media", "nes", "dat002", "covers", "front");
        File.WriteAllText(Path.Combine(dir, "metroid_001.tmp"),  "not an image");
        File.WriteAllText(Path.Combine(dir, "metroid_001.json"), "not an image");

        var result = MediaStore.FindCoverFront(_dataDir, "nes", "dat002", "Metroid");

        Assert.Null(result);
    }

    [Fact]
    public void FindCoverFront_AcceptsPng()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "gba", "dat003");
        var dir  = Path.Combine(_dataDir, "media", "gba", "dat003", "covers", "front");
        var file = Path.Combine(dir, "castlevania_001.png");
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47]);

        var result = MediaStore.FindCoverFront(_dataDir, "gba", "dat003", "Castlevania");

        Assert.Equal(file, result);
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

    // ── NextIndexedMediaPath ──────────────────────────────────────────────────

    [Fact]
    public void NextIndexedMediaPath_StartsAt001_WhenFolderEmpty()
    {
        var path = MediaStore.NextIndexedMediaPath(
            _dataDir, "snes", "dat001", "Super Mario World", Path.Combine("covers", "front"), "jpg");
        Assert.EndsWith("super_mario_world_001.jpg", path);
    }

    [Fact]
    public void NextIndexedMediaPath_IncrementsIndex_WhenFilesExist()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var dir = Path.Combine(_dataDir, "media", "snes", "dat001", "covers", "front");
        File.WriteAllText(Path.Combine(dir, "super_mario_world_001.jpg"), "");

        var path = MediaStore.NextIndexedMediaPath(
            _dataDir, "snes", "dat001", "Super Mario World", Path.Combine("covers", "front"), "png");
        Assert.EndsWith("super_mario_world_002.png", path);
    }

    // ── FindScreenshots ───────────────────────────────────────────────────────

    [Fact]
    public void FindScreenshots_EmptyDirectory_ReturnsEmpty()
    {
        MediaStore.EnsureMediaFolders(_dataDir, "snes", "dat001");
        var result = MediaStore.FindScreenshots(_dataDir, "snes", "dat001", "Super Mario World");
        Assert.Empty(result);
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
        var result = MediaStore.FindScreenshots(_dataDir, "snes", "nonexistent", "Any Game");
        Assert.Empty(result);
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
}
