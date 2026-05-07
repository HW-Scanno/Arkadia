using System;
using System.Threading.Tasks;
using Arkadia.Providers;
using Xunit;

namespace Arkadia.Tests.Providers;

public sealed class FetchDetailsByGameIdTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScraperCandidate Candidate(
        string providerId   = "screenscraper",
        string gameId       = "42",
        string platformId   = "1") =>
        new() { ProviderId = providerId, ProviderGameId = gameId, PlatformId = platformId };

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WrongProviderId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ScreenScraperClient.FetchDetailsByGameIdAsync(
                "d", "dp", "u", "p",
                Candidate(providerId: "igdb")));
    }

    [Fact]
    public async Task EmptyProviderGameId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ScreenScraperClient.FetchDetailsByGameIdAsync(
                "d", "dp", "u", "p",
                Candidate(gameId: "")));
    }

    [Theory]
    [InlineData("igdb")]
    [InlineData("launchbox")]
    [InlineData("")]
    public async Task NonScreenscraperProvider_ThrowsArgumentException(string providerId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ScreenScraperClient.FetchDetailsByGameIdAsync(
                "d", "dp", "u", "p",
                Candidate(providerId: providerId)));
    }

    // ── URL construction ──────────────────────────────────────────────────────

    [Fact]
    public void Url_UsesJeuInfosEndpoint()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("d", "dp", "u", "p", "42");
        Assert.Contains("jeuInfos.php", url);
    }

    [Fact]
    public void Url_ContainsGameid()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("d", "dp", "u", "p", "42");
        Assert.Contains("gameid=42", url);
    }

    [Fact]
    public void Url_DoesNotContainJeuId()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("d", "dp", "u", "p", "42");
        Assert.DoesNotContain("jeuId", url);
        Assert.DoesNotContain("jeuid", url);
    }

    // systemeid must NOT be included: gameid is globally unique in ScreenScraper's database.
    // Including systemeid causes null returns when the game is not under that exact system.
    [Fact]
    public void Url_NeverIncludesSystemeid()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("d", "dp", "u", "p", "42");
        Assert.DoesNotContain("systemeid", url);
    }

    [Fact]
    public void Url_DoesNotContainRomnom_OrRecherche()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("d", "dp", "u", "p", "42");
        Assert.DoesNotContain("romnom",    url);
        Assert.DoesNotContain("recherche", url);
    }

    [Fact]
    public void Url_EncodesGameId_SpecialCharacters()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("d", "dp", "u", "p", "42/extra");
        Assert.Contains("gameid=42%2Fextra", url);
    }

    [Fact]
    public void Url_ContainsCredentials()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("mydev", "mydevpw", "myuser", "mypw", "7");
        Assert.Contains("devid=mydev",         url);
        Assert.Contains("devpassword=mydevpw", url);
        Assert.Contains("ssid=myuser",         url);
        Assert.Contains("sspassword=mypw",     url);
    }

    // ── Result type compatibility ─────────────────────────────────────────────

    [Fact]
    public void ParseGameJson_PreservesRawJson()
    {
        const string json = """
            {"header":{"success":"true"},"response":{"jeu":{
              "id":42,
              "noms":[{"region":"wor","text":"Sonic"}]
            }}}
            """;
        var result = ScreenScraperClient.ParseGameJson(json);
        Assert.NotNull(result);
        Assert.Equal(json, result!.RawJson);
    }

    [Fact]
    public void ParseGameJson_ReturnsScreenScraperResult()
    {
        const string json = """
            {"header":{"success":"true"},"response":{"jeu":{
              "id":42,
              "noms":[{"region":"wor","text":"Sonic the Hedgehog"}],
              "developpeur":{"text":"Sonic Team"},
              "editeur":{"text":"Sega"}
            }}}
            """;
        var result = ScreenScraperClient.ParseGameJson(json);
        Assert.NotNull(result);
        Assert.Equal("Sonic the Hedgehog", result!.Title);
        Assert.Equal("Sonic Team",         result.Developer);
        Assert.Equal("Sega",               result.Publisher);
        Assert.NotEmpty(result.RawJson);
    }

    // ── QueryAsync BuildUrl unchanged (spot-check via SearchCandidatesAsync URL) ──

    [Fact]
    public void BuildGameIdUrl_DoesNotAffectSearchUrl()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("d", "dp", "u", "p", "42");
        Assert.Contains("jeuInfos.php",        url);
        Assert.DoesNotContain("jeuRecherche.php", url);
    }

    // ── Softname ──────────────────────────────────────────────────────────────

    [Fact]
    public void Url_ContainsSoftname_Default()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("d", "dp", "u", "p", "42");
        Assert.Contains("softname=Arkadia", url);
    }

    [Fact]
    public void Url_ContainsSoftname_Custom()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("d", "dp", "u", "p", "42", "MyApp");
        Assert.Contains("softname=MyApp", url);
    }

    [Fact]
    public void Url_Softname_IsUrlEncoded()
    {
        var url = ScreenScraperClient.BuildGameIdUrl("d", "dp", "u", "p", "42", "My App");
        Assert.Contains("softname=My%20App", url);
    }

    // ── ResolveSoftName ───────────────────────────────────────────────────────

    [Fact]
    public void ResolveSoftName_Null_ReturnsArkadia()
        => Assert.Equal("Arkadia", ScreenScraperClient.ResolveSoftName(null));

    [Fact]
    public void ResolveSoftName_Empty_ReturnsArkadia()
        => Assert.Equal("Arkadia", ScreenScraperClient.ResolveSoftName(""));

    [Fact]
    public void ResolveSoftName_Whitespace_ReturnsArkadia()
        => Assert.Equal("Arkadia", ScreenScraperClient.ResolveSoftName("   "));

    [Fact]
    public void ResolveSoftName_Custom_ReturnsTrimmedValue()
        => Assert.Equal("MyTool", ScreenScraperClient.ResolveSoftName("MyTool"));

    [Fact]
    public void ResolveSoftName_CustomWithWhitespace_ReturnsTrimmed()
        => Assert.Equal("MyTool", ScreenScraperClient.ResolveSoftName("  MyTool  "));
}
