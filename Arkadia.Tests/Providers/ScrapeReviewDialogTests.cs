using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arkadia;
using Arkadia.Providers;
using Xunit;

namespace Arkadia.Tests.Providers;

public sealed class ScrapeReviewDialogTests
{
    // ── BuildInitialQuery ─────────────────────────────────────────────────────

    [Fact]
    public void BuildInitialQuery_UsesCatalogTitle_WhenNonEmpty()
    {
        var q = ScrapeReviewDialog.BuildInitialQuery(
            "Sonic the Hedgehog", "Sonic the Hedgehog (World)");
        Assert.Equal("Sonic the Hedgehog", q);
    }

    [Fact]
    public void BuildInitialQuery_FallsBackToRawName_WhenCatalogTitleEmpty()
    {
        var q = ScrapeReviewDialog.BuildInitialQuery("", "Sonic the Hedgehog (World)");
        Assert.Equal("Sonic the Hedgehog (World)", q);
    }

    [Fact]
    public void BuildInitialQuery_ReturnsEmpty_WhenBothEmpty()
    {
        var q = ScrapeReviewDialog.BuildInitialQuery("", "");
        Assert.Equal("", q);
    }

    [Fact]
    public void BuildInitialQuery_IgnoresRawName_WhenCatalogTitlePresent()
    {
        var q = ScrapeReviewDialog.BuildInitialQuery("Super Mario Bros.", "Super Mario Bros. (W) [!]");
        Assert.Equal("Super Mario Bros.", q);
    }

    // ── QueryAsync still exists with original signature ───────────────────────

    [Fact]
    public void QueryAsync_StillExists_WithSevenParametersPlusCancellationTokenAndSoftName()
    {
        var method = typeof(ScreenScraperClient).GetMethod(
            nameof(ScreenScraperClient.QueryAsync));
        Assert.NotNull(method);
        // devId, devPassword, username, password, platformId, releaseName, isMame, ct, softName
        Assert.Equal(9, method!.GetParameters().Length);
    }

    [Fact]
    public void QueryAsync_SeventhParameter_IsNamedIsMame()
    {
        var method = typeof(ScreenScraperClient).GetMethod(
            nameof(ScreenScraperClient.QueryAsync));
        Assert.NotNull(method);
        var p = method!.GetParameters()[6];
        Assert.Equal("isMame", p.Name);
        Assert.Equal(typeof(bool), p.ParameterType);
    }

    // ── FetchDetailsByGameIdAsync exists ──────────────────────────────────────

    [Fact]
    public void FetchDetailsByGameIdAsync_Exists_WithCandidateParameter()
    {
        var method = typeof(ScreenScraperClient).GetMethod(
            nameof(ScreenScraperClient.FetchDetailsByGameIdAsync));
        Assert.NotNull(method);
        var candidateParam = method!.GetParameters()
            .FirstOrDefault(p => p.ParameterType == typeof(ScraperCandidate));
        Assert.NotNull(candidateParam);
    }

    // ── Integration: validation still rejects bad candidates ─────────────────

    [Fact]
    public async Task FetchDetailsByGameIdAsync_StillRejects_WrongProvider()
    {
        var candidate = new ScraperCandidate { ProviderId = "other", ProviderGameId = "1" };
        await Assert.ThrowsAsync<System.ArgumentException>(() =>
            ScreenScraperClient.FetchDetailsByGameIdAsync(
                "d", "dp", "u", "p", candidate, CancellationToken.None));
    }

    [Fact]
    public async Task FetchDetailsByGameIdAsync_StillRejects_EmptyGameId()
    {
        var candidate = new ScraperCandidate { ProviderId = "screenscraper", ProviderGameId = "" };
        await Assert.ThrowsAsync<System.ArgumentException>(() =>
            ScreenScraperClient.FetchDetailsByGameIdAsync(
                "d", "dp", "u", "p", candidate, CancellationToken.None));
    }

    // ── Auto-select logic ─────────────────────────────────────────────────────

    [Fact]
    public void ShouldAutoSelect_OneRow_ReturnsTrue()
        => Assert.True(ScrapeReviewDialog.ShouldAutoSelect(1));

    [Fact]
    public void ShouldAutoSelect_ZeroRows_ReturnsFalse()
        => Assert.False(ScrapeReviewDialog.ShouldAutoSelect(0));

    [Fact]
    public void ShouldAutoSelect_TwoRows_ReturnsFalse()
        => Assert.False(ScrapeReviewDialog.ShouldAutoSelect(2));

    [Fact]
    public void ShouldAutoSelect_ManyRows_ReturnsFalse()
        => Assert.False(ScrapeReviewDialog.ShouldAutoSelect(10));
}
