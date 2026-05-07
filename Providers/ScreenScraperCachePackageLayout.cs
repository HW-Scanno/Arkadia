namespace Arkadia.Providers;

public static class ScreenScraperCachePackageLayout
{
    public const string ManifestEntry  = "manifest.json";
    public const string GamesListEntry = "gameslist.csv";
    public const string PayloadsPrefix = "payloads/";
    public const string MediaPrefix    = "media/";

    public static string PayloadEntry(string gameId) => $"{PayloadsPrefix}{gameId}.json";
}
