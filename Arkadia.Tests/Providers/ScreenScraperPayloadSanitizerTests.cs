using System.Text.Json;
using Arkadia.Providers;
using Xunit;

namespace Arkadia.Tests.Providers;

public sealed class ScreenScraperPayloadSanitizerTests
{
    [Fact]
    public void SanitizeJson_EmptyString_ReturnsEmpty()
        => Assert.Equal("", ScreenScraperPayloadSanitizer.SanitizeJson(""));

    [Fact]
    public void SanitizeJson_NoCredentialParams_Unchanged()
    {
        const string json = """{"id":39874,"title":"1942","gameCount":10}""";
        Assert.Equal(json, ScreenScraperPayloadSanitizer.SanitizeJson(json));
    }

    [Fact]
    public void SanitizeJson_Devid_Replaced()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(
            """{"url":"https://ss.fr/api?devid=MYDEV&gameId=42"}""");
        Assert.DoesNotContain("MYDEV", result);
        Assert.Contains("<DEVID>", result);
    }

    [Fact]
    public void SanitizeJson_Devpassword_Replaced()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(
            """{"url":"?devpassword=secret123"}""");
        Assert.DoesNotContain("secret123", result);
        Assert.Contains("<DEVPASSWORD>", result);
    }

    [Fact]
    public void SanitizeJson_Ssid_Replaced()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(
            """{"url":"?ssid=myusername"}""");
        Assert.DoesNotContain("myusername", result);
        Assert.Contains("<SSID>", result);
    }

    [Fact]
    public void SanitizeJson_Sspassword_Replaced()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(
            """{"url":"?sspassword=mypassword"}""");
        Assert.DoesNotContain("mypassword", result);
        Assert.Contains("<SSPASSWORD>", result);
    }

    [Fact]
    public void SanitizeJson_Softname_Replaced()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(
            """{"url":"?softname=MyApp"}""");
        Assert.DoesNotContain("MyApp", result);
        Assert.Contains("<SOFTNAME>", result);
    }

    [Fact]
    public void SanitizeJson_AmpersandPrefix_AlsoReplaced()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(
            """{"url":"?gameId=42&devid=MYDEV&other=x"}""");
        Assert.DoesNotContain("MYDEV", result);
        Assert.Contains("<DEVID>", result);
        Assert.Contains("gameId=42", result);
        Assert.Contains("other=x", result);
    }

    [Fact]
    public void SanitizeJson_CaseInsensitive_Replaced()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(
            """{"url":"?DEVID=uppercased"}""");
        Assert.DoesNotContain("uppercased", result);
        Assert.Contains("<DEVID>", result);
    }

    [Fact]
    public void SanitizeJson_StopsAtNextAmpersand()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(
            """{"url":"?devid=ABC&gameId=99"}""");
        Assert.Contains("gameId=99", result);
        Assert.DoesNotContain("ABC", result);
    }

    [Fact]
    public void SanitizeJson_StopsAtQuote()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(
            """{"url":"?devid=ABC","other":"val"}""");
        Assert.DoesNotContain("ABC", result);
        Assert.Contains("\"other\":\"val\"", result);
    }

    [Fact]
    public void SanitizeJson_MultipleCredentialParams_AllReplaced()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(
            """{"url":"?devid=D&devpassword=DP&ssid=U&sspassword=P&softname=S&gameId=1"}""");
        Assert.Contains("<DEVID>",       result);
        Assert.Contains("<DEVPASSWORD>", result);
        Assert.Contains("<SSID>",        result);
        Assert.Contains("<SSPASSWORD>",  result);
        Assert.Contains("<SOFTNAME>",    result);
        Assert.Contains("gameId=1",      result);
    }

    [Fact]
    public void SanitizeJson_NonSensitiveParams_Preserved()
    {
        const string json = """{"url":"?gameId=39874&systemId=75"}""";
        Assert.Equal(json, ScreenScraperPayloadSanitizer.SanitizeJson(json));
    }

    // ── response.ssuser removal ───────────────────────────────────────────────

    private const string FullPayload = """
        {"response":{"ssuser":{"id":"Scanno","numid":"26962815","maxthreads":"1","requeststoday":"99"},"jeu":{"id":"39874","noms":[{"region":"wor","text":"1942"}]},"serveurs":{"closescraper":"0"}}}
        """;

    [Fact]
    public void SanitizeJson_RemovesSsuserFromResponse()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(FullPayload);
        Assert.DoesNotContain("ssuser", result);
    }

    [Fact]
    public void SanitizeJson_RemovesSsuserId()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(FullPayload);
        Assert.DoesNotContain("Scanno", result);
    }

    [Fact]
    public void SanitizeJson_RemovesSsuserNumId()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(FullPayload);
        Assert.DoesNotContain("26962815", result);
    }

    [Fact]
    public void SanitizeJson_KeepsResponseJeu()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(FullPayload);
        Assert.Contains("\"jeu\"", result);
        Assert.Contains("1942",    result);
    }

    [Fact]
    public void SanitizeJson_KeepsResponseServeurs()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(FullPayload);
        Assert.Contains("serveurs", result);
    }

    [Fact]
    public void SanitizeJson_SanitizedJsonIsValidJson()
    {
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(FullPayload);
        var ex = Record.Exception(() => JsonDocument.Parse(result));
        Assert.Null(ex);
    }

    [Fact]
    public void SanitizeJson_RemovesSsuser_AndReplacesCredentials()
    {
        const string json = """
            {"response":{"ssuser":{"id":"Scanno","numid":"12345"},"jeu":{"id":"39874","noms":[{"region":"wor","text":"1942"}],"medias":{"jeu_ss":[{"url":"?devid=REALDEV&ssid=REALUSER&gameId=39874"}]}}}}
            """;
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(json);

        Assert.DoesNotContain("ssuser",   result);
        Assert.DoesNotContain("Scanno",   result);
        Assert.DoesNotContain("REALDEV",  result);
        Assert.DoesNotContain("REALUSER", result);
        Assert.Contains("\"jeu\"",        result);
        Assert.Contains("<DEVID>",        result);
        Assert.Contains("<SSID>",         result);
    }

    [Fact]
    public void SanitizeJson_ParseGameJson_WorksOnSanitizedPayload()
    {
        const string json = """
            {"response":{"ssuser":{"id":"Scanno","numid":"12345"},"jeu":{"id":"39874","noms":[{"region":"wor","text":"1942"}],"developpeur":{"text":"Capcom"}}}}
            """;
        var sanitized = ScreenScraperPayloadSanitizer.SanitizeJson(json);
        var result    = ScreenScraperClient.ParseGameJson(sanitized);

        Assert.NotNull(result);
        Assert.Equal("1942",   result!.Title);
        Assert.Equal("Capcom", result.Developer);
    }

    [Fact]
    public void SanitizeJson_NoSsuser_LeavesJsonOtherwise_Unchanged()
    {
        // Payload without ssuser — sanitizer must not alter other content
        const string json = """{"response":{"jeu":{"id":"42","noms":[{"region":"wor","text":"Pac-Man"}]}}}""";
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(json);
        Assert.DoesNotContain("ssuser", result);
        Assert.Contains("Pac-Man", result);
        Assert.Contains("\"jeu\"", result);
    }

    [Fact]
    public void SanitizeJson_MalformedJson_FallsBackToRegexOnly()
    {
        // Not valid JSON — ssuser cannot be removed, but credential URL regex still applies.
        // The regex requires [?&] before the param name, so include a '?' in the input.
        const string broken = "not_valid_json but contains url: ?devid=MYDEV&gameId=42";
        var result = ScreenScraperPayloadSanitizer.SanitizeJson(broken);
        Assert.DoesNotContain("MYDEV", result);
        Assert.Contains("<DEVID>", result);
    }
}
