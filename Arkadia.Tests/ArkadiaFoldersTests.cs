using System;
using System.IO;
using Arkadia;
using Xunit;

namespace Arkadia.Tests;

public sealed class ArkadiaFoldersTests : IDisposable
{
    private readonly string _base;

    public ArkadiaFoldersTests()
    {
        _base = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    [Fact]
    public void EnsureCreated_Creates_IncomingCsv()
    {
        ArkadiaFolders.EnsureCreated(_base);
        Assert.True(Directory.Exists(Path.Combine(_base, ArkadiaFolders.IncomingCsv)));
    }

    [Fact]
    public void EnsureCreated_Creates_ScrapeCache()
    {
        ArkadiaFolders.EnsureCreated(_base);
        Assert.True(Directory.Exists(Path.Combine(_base, ArkadiaFolders.ScrapeCache)));
    }

    [Fact]
    public void EnsureCreated_Creates_ScrapeCacheScreenscraper()
    {
        ArkadiaFolders.EnsureCreated(_base);
        Assert.True(Directory.Exists(
            Path.Combine(_base, ArkadiaFolders.ScrapeCache, ArkadiaFolders.ScrapeCacheProvider)));
    }

    [Fact]
    public void EnsureCreated_Creates_StagingCache()
    {
        ArkadiaFolders.EnsureCreated(_base);
        Assert.True(Directory.Exists(Path.Combine(_base, ArkadiaFolders.StagingCache)));
    }

    [Fact]
    public void EnsureCreated_CalledTwice_IsIdempotent()
    {
        ArkadiaFolders.EnsureCreated(_base);
        var ex = Record.Exception(() => ArkadiaFolders.EnsureCreated(_base));
        Assert.Null(ex);
    }

    [Fact]
    public void IncomingCsv_Constant_MatchesExpectedName()
        => Assert.Equal("incoming-csv", ArkadiaFolders.IncomingCsv);

    [Fact]
    public void IncomingMedia_Constant_MatchesExpectedName()
        => Assert.Equal("incoming-media", ArkadiaFolders.IncomingMedia);

    [Fact]
    public void EnsureCreated_Creates_IncomingMedia()
    {
        ArkadiaFolders.EnsureCreated(_base);
        Assert.True(Directory.Exists(Path.Combine(_base, ArkadiaFolders.IncomingMedia)));
    }

    [Fact]
    public void DefaultOutputZipPath_IsUnderScrapeCacheScreenscraper()
    {
        var path = CacheBuilderHelper.DefaultOutputZipPath("test");
        Assert.StartsWith(
            Path.Combine(ArkadiaFolders.ScrapeCache, ArkadiaFolders.ScrapeCacheProvider),
            path);
    }

    [Fact]
    public void DefaultStagingRoot_MatchesStagingCacheConstant()
        => Assert.Equal(ArkadiaFolders.StagingCache, CacheBuilderHelper.DefaultStagingRoot);

    [Fact]
    public void EnsureCreated_ExistingFilesNotDeleted()
    {
        ArkadiaFolders.EnsureCreated(_base);
        var file = Path.Combine(_base, ArkadiaFolders.IncomingCsv, "test.csv");
        File.WriteAllText(file, "content");

        ArkadiaFolders.EnsureCreated(_base);

        Assert.True(File.Exists(file));
        Assert.Equal("content", File.ReadAllText(file));
    }
}
