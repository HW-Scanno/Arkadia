using Arkadia.Providers;
using Xunit;

namespace Arkadia.Tests.Providers;

public sealed class ScreenScraperCandidateTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string MakeJeux(string jeuxJson) =>
        """{"header":{"success":"true"},"response":{"jeux":[""" + jeuxJson + """]}}""";

    private static string MakeJeu(string jeuJson) =>
        """{"header":{"success":"true"},"response":{"jeu":""" + jeuJson + """}}""";

    private static string SimpleJeu(
        string id     = "42",
        string name   = "Sonic",
        string region = "wor") =>
        $$$"""
        {
          "id": {{{id}}},
          "noms": [{"region": "{{{region}}}", "text": "{{{name}}}"}],
          "systeme": {"id": "1", "text": "Mega Drive / Genesis"},
          "dates": [{"region": "wor", "text": "1991-06-23"}],
          "developpeur": {"text": "Sonic Team"},
          "editeur": {"text": "Sega"},
          "synopsis": [{"langue": "en", "text": "A fast hedgehog."}]
        }
        """;

    // ── Structural: jeux[] vs jeu vs empty ───────────────────────────────────

    [Fact]
    public void MultipleCandidates_ReturnsCorrectCount()
    {
        var json = MakeJeux(SimpleJeu("1", "Game A", "wor") + "," + SimpleJeu("2", "Game B", "us"));
        var result = ScreenScraperClient.ParseSearchJson(json);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SingleJeu_ReturnsSingleItemList()
    {
        var result = ScreenScraperClient.ParseSearchJson(MakeJeu(SimpleJeu()));
        Assert.Single(result);
    }

    [Fact]
    public void EmptyJeux_ReturnsEmpty()
    {
        const string json = """{"header":{"success":"true"},"response":{"jeux":[]}}""";
        Assert.Empty(ScreenScraperClient.ParseSearchJson(json));
    }

    [Fact]
    public void NoResponseProperty_ReturnsEmpty()
    {
        const string json = """{"header":{"success":"true"}}""";
        Assert.Empty(ScreenScraperClient.ParseSearchJson(json));
    }

    [Fact]
    public void ApiError_ReturnsEmpty()
    {
        const string json = """{"header":{"success":"false","error":"denied"}}""";
        Assert.Empty(ScreenScraperClient.ParseSearchJson(json));
    }

    [Fact]
    public void MalformedJson_ReturnsEmpty()
    {
        Assert.Empty(ScreenScraperClient.ParseSearchJson("not valid json {{{"));
    }

    // ── ProviderId ────────────────────────────────────────────────────────────

    [Fact]
    public void ProviderId_IsScreenscraper()
    {
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(SimpleJeu()))[0];
        Assert.Equal("screenscraper", candidate.ProviderId);
    }

    // ── ProviderGameId ────────────────────────────────────────────────────────

    [Fact]
    public void ProviderGameId_FromNumericId()
    {
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(SimpleJeu(id: "42")))[0];
        Assert.Equal("42", candidate.ProviderGameId);
    }

    [Fact]
    public void ProviderGameId_FromStringId()
    {
        const string jeuJson = """
            {
              "id": "99",
              "noms": [{"region": "wor", "text": "Test"}],
              "systeme": {"id": "1", "text": "Platform"}
            }
            """;
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(jeuJson))[0];
        Assert.Equal("99", candidate.ProviderGameId);
    }

    // ── Title and region preference ───────────────────────────────────────────

    [Fact]
    public void Title_PrefersWorRegion()
    {
        const string jeuJson = """
            {
              "id": 1,
              "noms": [
                {"region": "us",  "text": "US Title"},
                {"region": "wor", "text": "World Title"}
              ],
              "systeme": {"id": "1", "text": "Platform"}
            }
            """;
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(jeuJson))[0];
        Assert.Equal("World Title", candidate.Title);
    }

    [Fact]
    public void Title_FallsBackToFirstAvailable()
    {
        const string jeuJson = """
            {
              "id": 1,
              "noms": [{"region": "ss", "text": "SS Title"}],
              "systeme": {"id": "1", "text": "Platform"}
            }
            """;
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(jeuJson))[0];
        Assert.Equal("SS Title", candidate.Title);
    }

    // ── Year ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Year_ExtractedFromDateString()
    {
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(SimpleJeu()))[0];
        Assert.Equal("1991", candidate.Year);
    }

    // ── Developer and Publisher ───────────────────────────────────────────────

    [Fact]
    public void Developer_And_Publisher_Parsed()
    {
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(SimpleJeu()))[0];
        Assert.Equal("Sonic Team", candidate.Developer);
        Assert.Equal("Sega",       candidate.Publisher);
    }

    // ── Description ──────────────────────────────────────────────────────────

    [Fact]
    public void Description_PrefersEnglish()
    {
        const string jeuJson = """
            {
              "id": 1,
              "noms": [{"region": "wor", "text": "Game"}],
              "systeme": {"id": "1", "text": "Platform"},
              "synopsis": [
                {"langue": "fr", "text": "Description en français"},
                {"langue": "en", "text": "English description"}
              ]
            }
            """;
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(jeuJson))[0];
        Assert.Equal("English description", candidate.Description);
    }

    // ── Platform ──────────────────────────────────────────────────────────────

    [Fact]
    public void Platform_NameAndId_Parsed()
    {
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(SimpleJeu()))[0];
        Assert.Equal("Mega Drive / Genesis", candidate.PlatformName);
        Assert.Equal("1",                    candidate.PlatformId);
    }

    // ── Region ────────────────────────────────────────────────────────────────

    [Fact]
    public void Region_PrefersWor()
    {
        const string jeuJson = """
            {
              "id": 1,
              "noms": [
                {"region": "us",  "text": "US"},
                {"region": "wor", "text": "World"}
              ],
              "systeme": {"id": "1", "text": "Platform"}
            }
            """;
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(jeuJson))[0];
        Assert.Equal("wor", candidate.Region);
    }

    [Fact]
    public void Region_FallsBackToFirstAvailable()
    {
        const string jeuJson = """
            {
              "id": 1,
              "noms": [{"region": "jp", "text": "JP Title"}],
              "systeme": {"id": "1", "text": "Platform"}
            }
            """;
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(jeuJson))[0];
        Assert.Equal("jp", candidate.Region);
    }

    // ── Missing optional fields ───────────────────────────────────────────────

    [Fact]
    public void MissingOptionalFields_ReturnsEmptyStrings()
    {
        const string jeuJson = """{"id": 5, "systeme": {"id": "4", "text": "SNES"}}""";
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(jeuJson))[0];
        Assert.Equal("", candidate.Title);
        Assert.Equal("", candidate.Year);
        Assert.Equal("", candidate.Developer);
        Assert.Equal("", candidate.Publisher);
        Assert.Equal("", candidate.Description);
        Assert.Equal("", candidate.ThumbnailUrl);
        Assert.Equal("", candidate.Region);
    }

    // ── RawCandidateJson ──────────────────────────────────────────────────────

    [Fact]
    public void RawCandidateJson_IsNonEmpty()
    {
        var candidate = ScreenScraperClient.ParseSearchJson(MakeJeu(SimpleJeu()))[0];
        Assert.NotEmpty(candidate.RawCandidateJson);
    }
}
