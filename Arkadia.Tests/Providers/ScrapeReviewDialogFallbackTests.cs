using System.Collections.Generic;
using Arkadia;
using Arkadia.Providers;
using Xunit;

namespace Arkadia.Tests.Providers;

public sealed class ScrapeReviewDialogFallbackTests
{
    // ── ShouldAttemptRomFallback ──────────────────────────────────────────────

    [Fact]
    public void ShouldAttemptRomFallback_True_WhenCandidatesEmpty()
    {
        Assert.True(ScrapeReviewDialog.ShouldAttemptRomFallback([]));
    }

    [Fact]
    public void ShouldAttemptRomFallback_True_WhenAllCandidatesHaveEmptyTitle()
    {
        var candidates = new List<ScraperCandidate>
        {
            new() { ProviderId = "screenscraper", ProviderGameId = "1", Title = "" },
            new() { ProviderId = "screenscraper", ProviderGameId = "2", Title = "" },
        };
        Assert.True(ScrapeReviewDialog.ShouldAttemptRomFallback(candidates));
    }

    [Fact]
    public void ShouldAttemptRomFallback_False_WhenAtLeastOneCandidateHasTitle()
    {
        var candidates = new List<ScraperCandidate>
        {
            new() { ProviderId = "screenscraper", ProviderGameId = "1", Title = "" },
            new() { ProviderId = "screenscraper", ProviderGameId = "2", Title = "Sonic" },
        };
        Assert.False(ScrapeReviewDialog.ShouldAttemptRomFallback(candidates));
    }

    [Fact]
    public void ShouldAttemptRomFallback_False_WhenSingleCandidateHasTitle()
    {
        var candidates = new List<ScraperCandidate>
        {
            new() { ProviderId = "screenscraper", ProviderGameId = "42", Title = "Animal Basket" },
        };
        Assert.False(ScrapeReviewDialog.ShouldAttemptRomFallback(candidates));
    }

    // ── BuildSyntheticCandidate ───────────────────────────────────────────────

    [Fact]
    public void BuildSyntheticCandidate_UsesResultTitle_WhenNonEmpty()
    {
        var result = new ScreenScraperResult { Title = "Animal Basket" };
        var candidate = ScrapeReviewDialog.BuildSyntheticCandidate(result);
        Assert.Equal("Animal Basket", candidate.Title);
    }

    [Fact]
    public void BuildSyntheticCandidate_FallsBackToLabel_WhenTitleEmpty()
    {
        var result = new ScreenScraperResult { Title = "" };
        var candidate = ScrapeReviewDialog.BuildSyntheticCandidate(result);
        Assert.Equal("(Exact ROM match)", candidate.Title);
    }

    [Fact]
    public void BuildSyntheticCandidate_ProviderId_IsScreenscraperDirect()
    {
        var candidate = ScrapeReviewDialog.BuildSyntheticCandidate(new ScreenScraperResult());
        Assert.Equal("screenscraper-direct", candidate.ProviderId);
    }

    [Fact]
    public void BuildSyntheticCandidate_ProviderGameId_IsSentinel()
    {
        var candidate = ScrapeReviewDialog.BuildSyntheticCandidate(new ScreenScraperResult());
        Assert.Equal("__direct__", candidate.ProviderGameId);
    }

    [Fact]
    public void BuildSyntheticCandidate_CopiesMetadataFields()
    {
        var result = new ScreenScraperResult
        {
            Title       = "Blok Pong",
            Year        = "1976",
            Developer   = "Meadows Games",
            Publisher   = "Meadows Games",
            Description = "Early arcade classic.",
        };
        var candidate = ScrapeReviewDialog.BuildSyntheticCandidate(result);
        Assert.Equal("1976",          candidate.Year);
        Assert.Equal("Meadows Games", candidate.Developer);
        Assert.Equal("Meadows Games", candidate.Publisher);
        Assert.Equal("Early arcade classic.", candidate.Description);
    }

    // ── ScrapeReviewResult ────────────────────────────────────────────────────

    [Fact]
    public void ScrapeReviewResult_IsDirectResult_True_WhenDirectResultSet()
    {
        var r = new ScrapeReviewResult { DirectResult = new ScreenScraperResult() };
        Assert.True(r.IsDirectResult);
    }

    [Fact]
    public void ScrapeReviewResult_IsDirectResult_False_WhenOnlyCandidateSet()
    {
        var r = new ScrapeReviewResult
        {
            Candidate = new ScraperCandidate
            {
                ProviderId = "screenscraper", ProviderGameId = "42",
            },
        };
        Assert.False(r.IsDirectResult);
    }

    [Fact]
    public void ScrapeReviewResult_IsDirectResult_False_WhenBothNull()
    {
        Assert.False(new ScrapeReviewResult().IsDirectResult);
    }

    // ── QueryAsync signature unchanged ────────────────────────────────────────

    [Fact]
    public void QueryAsync_ParameterCount_IncludesSoftName()
    {
        var method = typeof(ScreenScraperClient)
            .GetMethod(nameof(ScreenScraperClient.QueryAsync));
        Assert.NotNull(method);
        // devId, devPassword, username, password, platformId, releaseName, isMame, ct, softName
        Assert.Equal(9, method!.GetParameters().Length);
    }

    [Fact]
    public void QueryAsync_SixthParameter_IsReleaseName()
    {
        var method = typeof(ScreenScraperClient)
            .GetMethod(nameof(ScreenScraperClient.QueryAsync));
        var p = method!.GetParameters()[5];
        Assert.Equal("releaseName", p.Name);
        Assert.Equal(typeof(string), p.ParameterType);
    }
}
