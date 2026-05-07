using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Arkadia.Providers;

/// <summary>
/// Abstracts a single-file media download so the cache package builder can be tested
/// without making real HTTP calls.
/// </summary>
public interface IMediaDownloader
{
    /// <summary>
    /// Downloads <paramref name="url"/> to a file whose path is <paramref name="destStem"/>
    /// plus a resolved extension.
    /// Returns the final saved path on success, or null when the file is skipped, empty,
    /// or has an unrecognised media type.
    /// Throws <see cref="ScreenScraperRateLimitException"/> on HTTP 429.
    /// Throws <see cref="OperationCanceledException"/> when cancellation is requested.
    /// </summary>
    Task<string?> DownloadAsync(
        string url,
        string destStem,
        string hintFormat,
        IReadOnlyList<string> validExts,
        long? expectedSize,
        CancellationToken ct);
}

/// <summary>
/// Production implementation — delegates to <see cref="ScreenScraperClient.DownloadMediaAsync"/>
/// and re-throws HTTP 429 as <see cref="ScreenScraperRateLimitException"/>.
/// </summary>
public sealed class ScreenScraperMediaDownloader : IMediaDownloader
{
    public async Task<string?> DownloadAsync(
        string url,
        string destStem,
        string hintFormat,
        IReadOnlyList<string> validExts,
        long? expectedSize,
        CancellationToken ct)
    {
        try
        {
            return await ScreenScraperClient.DownloadMediaAsync(url, destStem, hintFormat, validExts, expectedSize, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ScreenScraperRateLimitException();
        }
    }
}
