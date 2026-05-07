using System.Threading;
using System.Threading.Tasks;

namespace Arkadia.Providers;

/// <summary>
/// Abstracts the ScreenScraper game-detail fetch so the cache package builder
/// can be tested without making real HTTP calls.
/// </summary>
public interface IScreenScraperDetailsFetcher
{
    /// <summary>
    /// Fetches full game details for the given ScreenScraper game ID.
    /// Returns null when the game is not found.
    /// Throws <see cref="ScreenScraperRateLimitException"/> on HTTP 429.
    /// </summary>
    Task<ScreenScraperResult?> FetchAsync(string gameId, CancellationToken ct);
}

/// <summary>
/// Production implementation — delegates to <see cref="ScreenScraperClient.FetchDetailsByGameIdAsync"/>.
/// </summary>
public sealed class ScreenScraperDetailsFetcher(
    string devId,
    string devPassword,
    string username,
    string password,
    string systemId,
    string softName = ScreenScraperClient.DefaultSoftName) : IScreenScraperDetailsFetcher
{
    public Task<ScreenScraperResult?> FetchAsync(string gameId, CancellationToken ct)
    {
        var candidate = new ScraperCandidate
        {
            ProviderId     = "screenscraper",
            ProviderGameId = gameId,
            PlatformId     = systemId,
        };
        return ScreenScraperClient.FetchDetailsByGameIdAsync(
            devId, devPassword, username, password, candidate, ct, softName);
    }
}
