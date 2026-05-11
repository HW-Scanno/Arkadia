using System.IO;

namespace Arkadia;

public static class ArkadiaFolders
{
    public const string IncomingCsv         = "incoming-csv";
    public const string IncomingMedia        = "incoming-media";
    public const string ScrapeCache         = "scrape-cache";
    public const string ScrapeCacheProvider = "screenscraper";
    public const string ArkadiaMediaPacks   = "arkadia-media-packs";
    public const string StagingCache        = "staging-cache";
    public const string Backups             = "backups";

    public static void EnsureCreated(string baseDir)
    {
        Directory.CreateDirectory(Path.Combine(baseDir, IncomingCsv));
        Directory.CreateDirectory(Path.Combine(baseDir, IncomingMedia));
        Directory.CreateDirectory(Path.Combine(baseDir, ScrapeCache));
        Directory.CreateDirectory(Path.Combine(baseDir, ScrapeCache, ScrapeCacheProvider));
        Directory.CreateDirectory(Path.Combine(baseDir, ScrapeCache, ArkadiaMediaPacks));
        Directory.CreateDirectory(Path.Combine(baseDir, StagingCache));
        Directory.CreateDirectory(Path.Combine(baseDir, Backups));
    }
}
