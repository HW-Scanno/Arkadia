using System;
using System.IO;
using Arkadia;
using Arkadia.Providers;
using Xunit;

namespace Arkadia.Tests.Providers;

public sealed class CacheBuilderHelperTests : IDisposable
{
    private readonly string _tempFile;

    public CacheBuilderHelperTests()
    {
        _tempFile = Path.GetTempFileName();
    }

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { }
    }

    // ── SanitizePackageName ───────────────────────────────────────────────────

    [Fact]
    public void Sanitize_SafeName_Unchanged()
        => Assert.Equal("capcom-classics", CacheBuilderHelper.SanitizePackageName("capcom-classics"));

    [Fact]
    public void Sanitize_Space_ReplacedWithDash()
        => Assert.Equal("capcom-classics", CacheBuilderHelper.SanitizePackageName("capcom classics"));

    [Fact]
    public void Sanitize_Colon_ReplacedWithDash()
        => Assert.Equal("arcade-test", CacheBuilderHelper.SanitizePackageName("arcade:test"));

    [Fact]
    public void Sanitize_Slash_ReplacedWithDash()
        => Assert.Equal("a-b", CacheBuilderHelper.SanitizePackageName("a/b"));

    [Fact]
    public void Sanitize_MultipleConsecutiveUnsafe_CollapsedToDash()
        => Assert.Equal("a-b", CacheBuilderHelper.SanitizePackageName("a  b"));

    [Fact]
    public void Sanitize_LeadingTrailingUnsafe_Trimmed()
        => Assert.Equal("test", CacheBuilderHelper.SanitizePackageName(" test "));

    [Fact]
    public void Sanitize_EmptyString_ReturnedUnchanged()
        => Assert.Equal("", CacheBuilderHelper.SanitizePackageName(""));

    [Fact]
    public void Sanitize_Underscore_IsAllowed()
        => Assert.Equal("arcade_capcom", CacheBuilderHelper.SanitizePackageName("arcade_capcom"));

    // ── DefaultOutputZipPath ──────────────────────────────────────────────────

    [Fact]
    public void DefaultOutputZipPath_ContainsCacheScraperPrefix()
    {
        var path = CacheBuilderHelper.DefaultOutputZipPath("test-package");
        Assert.StartsWith(Path.Combine("scrape-cache", "screenscraper"), path);
    }

    [Fact]
    public void DefaultOutputZipPath_EndsWithZip()
    {
        var path = CacheBuilderHelper.DefaultOutputZipPath("test-package");
        Assert.EndsWith(".zip", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultOutputZipPath_ContainsPackageName()
    {
        var path = CacheBuilderHelper.DefaultOutputZipPath("my-package");
        Assert.Contains("my-package", path);
    }

    // ── DefaultStagingRoot ────────────────────────────────────────────────────

    [Fact]
    public void DefaultStagingRoot_IsRelativePath()
        => Assert.False(Path.IsPathRooted(CacheBuilderHelper.DefaultStagingRoot));

    [Fact]
    public void DefaultStagingRoot_ContainsStagingCache()
        => Assert.Contains("staging-cache", CacheBuilderHelper.DefaultStagingRoot);

    // ── Validate ──────────────────────────────────────────────────────────────

    private static string? CallValidate(
        string csvPath       = "",
        string systemId      = "75",
        string systemName    = "Capcom",
        string packageName   = "test-pkg",
        string outputZip     = "/out/test.zip",
        string stagingRoot   = "/staging",
        int    maxScrapes    = 100,
        bool   credentials   = true,
        string softname      = "Arkadia",
        Func<string, bool>? fileExists = null)
        => CacheBuilderHelper.Validate(csvPath, systemId, systemName, packageName,
                                       outputZip, stagingRoot, maxScrapes, credentials,
                                       softname, fileExists);

    [Fact]
    public void Validate_AllValid_ReturnsNull()
    {
        var err = CallValidate(csvPath: _tempFile, fileExists: _ => true);
        Assert.Null(err);
    }

    [Fact]
    public void Validate_MissingCsv_ReturnsError()
    {
        var err = CallValidate(csvPath: "/nonexistent/file.csv", fileExists: _ => false);
        Assert.NotNull(err);
        Assert.Contains("CSV", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_EmptySystemId_ReturnsError()
    {
        var err = CallValidate(csvPath: _tempFile, systemId: "", fileExists: _ => true);
        Assert.NotNull(err);
        Assert.Contains("System ID", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_EmptySystemName_ReturnsError()
    {
        var err = CallValidate(csvPath: _tempFile, systemName: "", fileExists: _ => true);
        Assert.NotNull(err);
        Assert.Contains("System Name", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_EmptyPackageName_ReturnsError()
    {
        var err = CallValidate(csvPath: _tempFile, packageName: "", fileExists: _ => true);
        Assert.NotNull(err);
        Assert.Contains("Package Name", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_EmptyOutputZip_ReturnsError()
    {
        var err = CallValidate(csvPath: _tempFile, outputZip: "", fileExists: _ => true);
        Assert.NotNull(err);
        Assert.Contains("Output ZIP", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_EmptyStagingRoot_ReturnsError()
    {
        var err = CallValidate(csvPath: _tempFile, stagingRoot: "", fileExists: _ => true);
        Assert.NotNull(err);
        Assert.Contains("Staging root", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MaxScrapesZero_ReturnsError()
    {
        var err = CallValidate(csvPath: _tempFile, maxScrapes: 0, fileExists: _ => true);
        Assert.NotNull(err);
        Assert.Contains("Max scrapes", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MaxScrapesNegative_ReturnsError()
    {
        var err = CallValidate(csvPath: _tempFile, maxScrapes: -5, fileExists: _ => true);
        Assert.NotNull(err);
    }

    [Fact]
    public void Validate_MaxScrapesOne_IsValid()
    {
        var err = CallValidate(csvPath: _tempFile, maxScrapes: 1, fileExists: _ => true);
        Assert.Null(err);
    }

    [Fact]
    public void Validate_CredentialsNotConfigured_ReturnsError()
    {
        var err = CallValidate(csvPath: _tempFile, credentials: false, fileExists: _ => true);
        Assert.NotNull(err);
        Assert.Contains("credentials", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_EmptySoftname_ReturnsError()
    {
        var err = CallValidate(csvPath: _tempFile, softname: "", fileExists: _ => true);
        Assert.NotNull(err);
        Assert.Contains("Softname", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_WhitespaceSoftname_ReturnsError()
    {
        var err = CallValidate(csvPath: _tempFile, softname: "   ", fileExists: _ => true);
        Assert.NotNull(err);
        Assert.Contains("Softname", err, StringComparison.OrdinalIgnoreCase);
    }

    // ── GetResultStatus ───────────────────────────────────────────────────────

    private static ScreenScraperCachePackageBuildResult MakeResult(
        bool wasAlreadyBuilt   = false,
        bool isComplete        = false,
        bool hitSafeLimit      = false,
        bool hitRateLimit      = false,
        int  validGames        = 10,
        int  payloadsAvailable = 0,
        int  remainingPayloads = 10,
        int  failedFetches     = 0,
        int  mediaWritten      = 0,
        int  alreadyStagedMedia = 0)
        => new(
            OutputZipPath:        isComplete ? "/out.zip" : "",
            StagingPath:          "/staging",
            ValidGames:           validGames,
            PayloadsWritten:      0,
            AlreadyStaged:        0,
            PayloadsAvailable:    payloadsAvailable,
            RemainingPayloads:    remainingPayloads,
            SkippedRows:          0,
            FailedFetches:        failedFetches,
            MediaWritten:         mediaWritten,
            AlreadyStagedMedia:   alreadyStagedMedia,
            FailedMediaDownloads: 0,
            HitRateLimit:         hitRateLimit,
            HitSafeLimit:         hitSafeLimit,
            IsComplete:           isComplete,
            WasAlreadyBuilt:      wasAlreadyBuilt);

    [Fact]
    public void GetResultStatus_WasAlreadyBuilt_MentionsExisting()
    {
        var s = CacheBuilderHelper.GetResultStatus(MakeResult(wasAlreadyBuilt: true));
        Assert.Contains("already exists", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetResultStatus_IsComplete_MentionsSuccess()
    {
        var s = CacheBuilderHelper.GetResultStatus(
            MakeResult(isComplete: true, payloadsAvailable: 5, mediaWritten: 10));
        Assert.Contains("success", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5", s);
        Assert.Contains("10", s);
    }

    [Fact]
    public void GetResultStatus_HitSafeLimit_MentionsSafeLimit()
    {
        var s = CacheBuilderHelper.GetResultStatus(MakeResult(hitSafeLimit: true));
        Assert.Contains("safe limit", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetResultStatus_HitRateLimit_MentionsRateLimit()
    {
        var s = CacheBuilderHelper.GetResultStatus(MakeResult(hitRateLimit: true));
        Assert.Contains("rate limit", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetResultStatus_Incomplete_MentionsRemaining()
    {
        var s = CacheBuilderHelper.GetResultStatus(
            MakeResult(validGames: 10, remainingPayloads: 7, payloadsAvailable: 3));
        Assert.Contains("incomplete", s, StringComparison.OrdinalIgnoreCase);
    }
}
