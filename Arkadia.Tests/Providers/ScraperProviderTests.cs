using Arkadia;
using Xunit;

namespace Arkadia.Tests.Providers;

/// <summary>
/// Tests for ScraperProviderInfo availability detection.
/// Dialog behavior (selection, cancel) is UI-only and covered by code review.
/// </summary>
public sealed class ScraperProviderTests
{
    // ── IsScreenScraperConfigured ────────────────────────────────────────────

    [Fact]
    public void AllCredentialsPresent_IsConfigured()
    {
        Assert.True(ScraperProviderInfo.IsScreenScraperConfigured(
            "user", "pass", "devid", "devpass"));
    }

    [Theory]
    [InlineData("",     "pass",  "devid",  "devpass")]
    [InlineData("user", "",      "devid",  "devpass")]
    [InlineData("user", "pass",  "",       "devpass")]
    [InlineData("user", "pass",  "devid",  "")]
    [InlineData("",     "",      "",       "")]
    public void AnyMissingCredential_NotConfigured(
        string u, string p, string devId, string devPw)
    {
        Assert.False(ScraperProviderInfo.IsScreenScraperConfigured(u, p, devId, devPw));
    }

    // ── ScraperProviderInfo properties ───────────────────────────────────────

    [Fact]
    public void Available_StatusText_IsAvailable()
    {
        var info = new ScraperProviderInfo("screenscraper", "ScreenScraper", true);
        Assert.Equal("Available", info.StatusText);
    }

    [Fact]
    public void NotAvailable_StatusText_IsNotConfigured()
    {
        var info = new ScraperProviderInfo("screenscraper", "ScreenScraper", false);
        Assert.Equal("Not configured", info.StatusText);
    }

    [Fact]
    public void Available_IsAvailableTrue()
    {
        var configured = ScraperProviderInfo.IsScreenScraperConfigured(
            "user", "pass", "devid", "devpass");
        var info = new ScraperProviderInfo("screenscraper", "ScreenScraper", configured);
        Assert.True(info.IsAvailable);
    }

    [Fact]
    public void NotAvailable_IsAvailableFalse()
    {
        var configured = ScraperProviderInfo.IsScreenScraperConfigured(
            "", "pass", "devid", "devpass");
        var info = new ScraperProviderInfo("screenscraper", "ScreenScraper", configured);
        Assert.False(info.IsAvailable);
    }

    [Fact]
    public void ProviderId_RoundTrips()
    {
        var info = new ScraperProviderInfo("screenscraper", "ScreenScraper", true);
        Assert.Equal("screenscraper", info.ProviderId);
    }

    [Fact]
    public void DisplayName_RoundTrips()
    {
        var info = new ScraperProviderInfo("screenscraper", "ScreenScraper", true);
        Assert.Equal("ScreenScraper", info.DisplayName);
    }
}
