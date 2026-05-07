using System;
using System.IO;
using System.Linq;
using Arkadia.Providers;

namespace Arkadia;

/// <summary>
/// Pure static helpers for the ScreenScraper Cache Builder dialog.
/// Extracted to keep business logic unit-testable without a UI context.
/// </summary>
public static class CacheBuilderHelper
{
    private static readonly char[] UnsafeChars =
        Path.GetInvalidFileNameChars()
            .Append(' ')
            .Distinct()
            .ToArray();

    /// <summary>
    /// Replaces characters that are unsafe in a filename with dashes and
    /// collapses consecutive dashes. Trims leading/trailing dashes.
    /// </summary>
    public static string SanitizePackageName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var chars = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            chars[i] = Array.IndexOf(UnsafeChars, name[i]) >= 0 ? '-' : name[i];
        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal))
            s = s.Replace("--", "-");
        return s.Trim('-');
    }

    /// <summary>
    /// Default relative output ZIP path for a sanitized package name.
    /// Caller must resolve to an absolute path before use.
    /// </summary>
    public static string DefaultOutputZipPath(string sanitizedPackageName)
        => Path.Combine(ArkadiaFolders.ScrapeCache, ArkadiaFolders.ScrapeCacheProvider, sanitizedPackageName + ".zip");

    /// <summary>Default relative staging root path.</summary>
    public const string DefaultStagingRoot = "staging-cache";

    /// <summary>
    /// Validates the build options. Returns null when all options are valid,
    /// or an error message string when validation fails.
    /// <paramref name="fileExists"/> can be injected for testing.
    /// </summary>
    public static string? Validate(
        string csvPath,
        string systemId,
        string systemName,
        string packageName,
        string outputZipPath,
        string stagingRoot,
        int    maxScrapes,
        bool   credentialsConfigured,
        string softname             = "",
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;

        if (!fileExists(csvPath))
            return "CSV file does not exist.";
        if (string.IsNullOrWhiteSpace(systemId))
            return "System ID is required.";
        if (string.IsNullOrWhiteSpace(systemName))
            return "System Name is required.";
        if (string.IsNullOrWhiteSpace(packageName))
            return "Package Name is required.";
        if (string.IsNullOrWhiteSpace(outputZipPath))
            return "Output ZIP path is required.";
        if (string.IsNullOrWhiteSpace(stagingRoot))
            return "Staging root is required.";
        if (maxScrapes < 1)
            return "Max scrapes per run must be at least 1.";
        if (!credentialsConfigured)
            return "ScreenScraper credentials are not configured. Go to Providers \u2192 ROM Scrapers.";
        if (string.IsNullOrWhiteSpace(softname))
            return "ScreenScraper Softname is required.";
        return null;
    }

    /// <summary>
    /// Maps a completed build result to a compact one-line status string for
    /// display in the dialog status area.
    /// </summary>
    public static string GetResultStatus(ScreenScraperCachePackageBuildResult r)
    {
        if (r.WasAlreadyBuilt)
            return "Package already exists. Use 'Force rebuild' to rebuild.";
        if (r.IsComplete)
            return $"Package built successfully. Payloads: {r.PayloadsAvailable}  Media: {r.MediaWritten + r.AlreadyStagedMedia}";
        if (r.HitSafeLimit)
            return "Paused at safe limit.";
        if (r.HitRateLimit)
            return "Rate limit reached.";
        return $"Build incomplete. Valid: {r.ValidGames}, Available: {r.PayloadsAvailable}, Remaining: {r.RemainingPayloads}, Failed: {r.FailedFetches}";
    }
}
