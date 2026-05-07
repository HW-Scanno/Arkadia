using System.IO;

namespace Arkadia;

public static class ArkadiaFolders
{
    public const string IncomingCsv         = "incoming-csv";
    public const string ScrapeCache         = "scrape-cache";
    public const string ScrapeCacheProvider = "screenscraper";
    public const string StagingCache        = "staging-cache";

    public static void EnsureCreated(string baseDir)
    {
        Directory.CreateDirectory(Path.Combine(baseDir, IncomingCsv));
        Directory.CreateDirectory(Path.Combine(baseDir, ScrapeCache));
        Directory.CreateDirectory(Path.Combine(baseDir, ScrapeCache, ScrapeCacheProvider));
        Directory.CreateDirectory(Path.Combine(baseDir, StagingCache));
    }
}
