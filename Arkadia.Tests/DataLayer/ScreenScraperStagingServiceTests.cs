using System;
using System.IO;
using System.Threading;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class ScreenScraperStagingServiceTests : IDisposable
{
    private readonly string _base;

    // Staging provider root: _base/staging-cache/screenscraper/
    private string StagingRoot => Path.Combine(
        _base, ArkadiaFolders.StagingCache, ArkadiaFolders.ScrapeCacheProvider);

    // scrape-cache root: _base/scrape-cache/screenscraper/
    private string ScrapeCacheRoot => Path.Combine(
        _base, ArkadiaFolders.ScrapeCache, ArkadiaFolders.ScrapeCacheProvider);

    private ScreenScraperStagingService Svc => new(_base);

    public ScreenScraperStagingServiceTests()
    {
        _base = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(StagingRoot);
        Directory.CreateDirectory(ScrapeCacheRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string MakePackage(string name)
    {
        var dir = Path.Combine(StagingRoot, name);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "payloads"));
        Directory.CreateDirectory(Path.Combine(dir, "media"));
        return dir;
    }

    private static void WriteCsv(string dir, int gameCount)
    {
        using var w = new StreamWriter(Path.Combine(dir, "gameslist.csv"));
        w.WriteLine("\"Game ID\";\"Game Name\"");
        for (int i = 1; i <= gameCount; i++)
            w.WriteLine($"\"{i}\";\"Game {i}\"");
    }

    private static void WritePayload(string dir, string gameId)
        => File.WriteAllText(Path.Combine(dir, "payloads", $"{gameId}.json"), "{}");

    private static void WriteMedia(string dir, string subdir, string filename, int bytes = 1024)
    {
        var path = Path.Combine(dir, "media", subdir);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, filename), new byte[bytes]);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_EmptyStagingRoot_ReturnsEmptyList()
    {
        var records = Svc.LoadStagingRecords();
        Assert.Empty(records);
    }

    [Fact]
    public void Load_CsvWithNoPayloads_Reports0PercentResumable()
    {
        var dir = MakePackage("testpkg");
        WriteCsv(dir, 4);

        var records = Svc.LoadStagingRecords();

        Assert.Single(records);
        Assert.Equal("Resumable", records[0].Status);
        Assert.Equal(0, records[0].PayloadCount);
        Assert.Equal(4, records[0].TotalGames);
        Assert.Equal(0.0, records[0].CompletionPercent, precision: 3);
    }

    [Fact]
    public void Load_TwoOfFourPayloads_Reports50Percent()
    {
        var dir = MakePackage("testpkg");
        WriteCsv(dir, 4);
        WritePayload(dir, "1");
        WritePayload(dir, "2");

        var rec = Assert.Single(Svc.LoadStagingRecords());
        Assert.Equal(50.0, rec.CompletionPercent, precision: 3);
        Assert.Equal(2, rec.PayloadCount);
        Assert.Equal("Resumable", rec.Status);
    }

    [Fact]
    public void Load_AllPayloads_ReportsComplete()
    {
        var dir = MakePackage("testpkg");
        WriteCsv(dir, 2);
        WritePayload(dir, "1");
        WritePayload(dir, "2");

        var rec = Assert.Single(Svc.LoadStagingRecords());
        Assert.Equal("Complete", rec.Status);
        Assert.Equal(100.0, rec.CompletionPercent, precision: 3);
    }

    [Fact]
    public void Load_MediaFilesCountedRecursively()
    {
        var dir = MakePackage("testpkg");
        WriteCsv(dir, 1);
        WriteMedia(dir, "boxart",     "1_0.jpg");
        WriteMedia(dir, "screenshot", "1_0.jpg");
        WriteMedia(dir, "screenshot", "1_1.png");

        var rec = Assert.Single(Svc.LoadStagingRecords());
        Assert.Equal(3, rec.MediaFileCount);
    }

    [Fact]
    public void Load_SizeBytesIncludesNestedFiles()
    {
        var dir = MakePackage("testpkg");
        WriteCsv(dir, 1);
        WriteMedia(dir, "boxart", "1_0.jpg", bytes: 2048);
        WritePayload(dir, "1"); // small JSON

        var rec = Assert.Single(Svc.LoadStagingRecords());
        Assert.True(rec.SizeBytes >= 2048, $"Expected SizeBytes >= 2048, got {rec.SizeBytes}");
    }

    [Fact]
    public void Load_LastUpdatedReflectsNewestFile()
    {
        var dir = MakePackage("testpkg");
        WriteCsv(dir, 1);

        var newerTime = DateTime.UtcNow.AddHours(-1);
        var mediaFile = Path.Combine(dir, "media", "boxart", "1_0.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(mediaFile)!);
        File.WriteAllBytes(mediaFile, new byte[100]);
        File.SetLastWriteTimeUtc(mediaFile, newerTime);

        var rec = Assert.Single(Svc.LoadStagingRecords());
        Assert.NotNull(rec.LastUpdatedUtc);
        Assert.True(rec.LastUpdatedUtc >= newerTime.AddSeconds(-2));
    }

    [Fact]
    public void LoadTopBySize_ReturnsSortedBySizeDescending()
    {
        for (int i = 1; i <= 6; i++)
        {
            var dir = MakePackage($"pkg{i:D2}");
            WriteCsv(dir, 1);
            WriteMedia(dir, "boxart", "1_0.jpg", bytes: i * 1024);
        }

        var top = Svc.LoadTopBySize(5);

        Assert.Equal(5, top.Count);
        // Largest first: pkg06 (6KB), pkg05 (5KB), ...
        Assert.True(top[0].SizeBytes >= top[1].SizeBytes);
        Assert.True(top[1].SizeBytes >= top[2].SizeBytes);
        Assert.True(top[2].SizeBytes >= top[3].SizeBytes);
        Assert.True(top[3].SizeBytes >= top[4].SizeBytes);
    }

    [Fact]
    public void DeleteStaging_RemovesSelectedFolder()
    {
        var dir = MakePackage("testpkg");
        WriteCsv(dir, 1);

        Assert.True(Directory.Exists(dir));
        Svc.DeleteStaging(dir);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteStaging_RefusesPathOutsideStagingRoot()
    {
        var outside = Path.Combine(_base, "incoming-csv");
        Directory.CreateDirectory(outside);

        Assert.Throws<ArgumentException>(() => Svc.DeleteStaging(outside));
    }

    [Fact]
    public void DeleteStaging_RefusesProviderRootItself()
    {
        Assert.Throws<ArgumentException>(() => Svc.DeleteStaging(StagingRoot));
    }

    [Fact]
    public void Load_UnknownOrEmptyFolder_DoesNotCrash()
    {
        // Folder with no gameslist.csv and no payloads
        Directory.CreateDirectory(Path.Combine(StagingRoot, "orphan"));

        var records = Svc.LoadStagingRecords();
        var rec = Assert.Single(records);
        Assert.True(rec.Status is "Empty" or "Unknown");
    }

    [Fact]
    public void Load_CalledTwice_IsIdempotent()
    {
        var dir = MakePackage("testpkg");
        WriteCsv(dir, 2);
        WritePayload(dir, "1");

        var first  = Svc.LoadStagingRecords();
        var second = Svc.LoadStagingRecords();

        Assert.Equal(first.Count,                           second.Count);
        Assert.Equal(first[0].PayloadCount,                 second[0].PayloadCount);
        Assert.Equal(first[0].CompletionPercent,            second[0].CompletionPercent);
    }

    [Fact]
    public void DeleteStaging_DoesNotTouchZipUnderScrapeCache()
    {
        var stagingDir = MakePackage("mypkg");
        WriteCsv(stagingDir, 1);

        var zipPath = Path.Combine(ScrapeCacheRoot, "mypkg.zip");
        File.WriteAllBytes(zipPath, new byte[512]);

        Svc.DeleteStaging(stagingDir);

        Assert.False(Directory.Exists(stagingDir), "Staging folder should be deleted");
        Assert.True(File.Exists(zipPath),           "ZIP package must not be touched");
    }
}
