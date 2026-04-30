using Arkadia.Providers;
using System.Text.Json;
using Xunit;

namespace Arkadia.Tests.Providers;

/// <summary>
/// Tests for ScreenScraperClient.ParseGameJson — metadata quality:
/// HTML entity decoding, date region priority, language extraction,
/// and graceful handling of missing/malformed fields.
/// </summary>
public sealed class ScreenScraperParserTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps a jeu-object fragment in the minimal envelope ParseGameJson expects.
    /// </summary>
    private static string Wrap(string jeuJson)
        => "{\"response\":{\"jeu\":" + jeuJson + "}}";

    // ── HTML entity decoding ──────────────────────────────────────────────────

    [Fact]
    public void Title_HtmlEntities_AreDecoded()
    {
        var json = Wrap("""
            {
              "noms": [{ "region": "wor", "text": "Pok&#233;mon Red &amp; Blue" }],
              "developpeur": { "text": "" }, "editeur": { "text": "" },
              "dates": [], "synopsis": [], "langues": [], "medias": []
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.NotNull(result);
        Assert.Equal("Pokémon Red & Blue", result!.Title);
    }

    [Fact]
    public void Developer_HtmlEntities_AreDecoded()
    {
        var json = Wrap("""
            {
              "noms": [],
              "developpeur": { "text": "Rare &amp; Associates" },
              "editeur": { "text": "" },
              "dates": [], "synopsis": [], "langues": [], "medias": []
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.NotNull(result);
        Assert.Equal("Rare & Associates", result!.Developer);
    }

    [Fact]
    public void Description_HtmlEntities_AreDecoded()
    {
        var json = Wrap("""
            {
              "noms": [],
              "developpeur": { "text": "" }, "editeur": { "text": "" },
              "dates": [], "langues": [], "medias": [],
              "synopsis": [{ "langue": "en", "text": "A hero&#39;s &lt;journey&gt;" }]
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.NotNull(result);
        Assert.Equal("A hero's <journey>", result!.Description);
    }

    // ── Date region priority ──────────────────────────────────────────────────

    [Fact]
    public void Year_PrefersWor_OverUs()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "synopsis": [], "langues": [], "medias": [],
              "dates": [
                { "region": "us",  "text": "1991-08-01" },
                { "region": "wor", "text": "1990-11-21" }
              ]
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.Equal("1990", result!.Year);
    }

    [Fact]
    public void Year_FallsBackToUs_WhenNoWor()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "synopsis": [], "langues": [], "medias": [],
              "dates": [
                { "region": "eu", "text": "1992-03-01" },
                { "region": "us", "text": "1991-08-01" }
              ]
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.Equal("1991", result!.Year);
    }

    [Fact]
    public void Year_FallsBackToJp_WhenNoWorUsEu()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "synopsis": [], "langues": [], "medias": [],
              "dates": [
                { "region": "jp", "text": "1989-04-21" }
              ]
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.Equal("1989", result!.Year);
    }

    [Fact]
    public void Year_FallsBackToFirst_WhenNoKnownRegion()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "synopsis": [], "langues": [], "medias": [],
              "dates": [
                { "region": "au", "text": "1993-06-15" }
              ]
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.Equal("1993", result!.Year);
    }

    [Fact]
    public void Year_ReturnsEmpty_WhenNoDates()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "synopsis": [], "langues": [], "medias": [],
              "dates": []
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.Equal("", result!.Year);
    }

    [Fact]
    public void Year_RejectsNonDigitPrefix()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "synopsis": [], "langues": [], "medias": [],
              "dates": [
                { "region": "wor", "text": "TBD-01-01" }
              ]
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.Equal("", result!.Year);
    }

    // ── Language extraction ───────────────────────────────────────────────────

    [Fact]
    public void Languages_ExtractedAsUppercaseCommaSeparated()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "synopsis": [], "dates": [], "medias": [],
              "langues": [
                { "shortname": "en", "nom": "English" },
                { "shortname": "fr", "nom": "French" },
                { "shortname": "de", "nom": "German" }
              ]
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.NotNull(result);
        Assert.Equal("EN, FR, DE", result!.Languages);
    }

    [Fact]
    public void Languages_EmptyArray_ReturnsEmptyString()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "synopsis": [], "dates": [], "medias": [],
              "langues": []
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.Equal("", result!.Languages);
    }

    [Fact]
    public void Languages_FieldAbsent_ReturnsEmptyString()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "synopsis": [], "dates": [], "medias": []
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.Equal("", result!.Languages);
    }

    // ── PickLanguages unit test (tests internal helper directly) ──────────────

    [Fact]
    public void PickLanguages_SingleEntry_ReturnsUppercaseCode()
    {
        using var doc = JsonDocument.Parse("""{ "langues": [{ "shortname": "ja" }] }""");
        var lang = ScreenScraperClient.PickLanguages(doc.RootElement);
        Assert.Equal("JA", lang);
    }

    // ── Missing / malformed fields do not crash ───────────────────────────────

    [Fact]
    public void AllFieldsMissing_ReturnsEmptyResult_NotNull()
    {
        var json = Wrap("{}");
        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.NotNull(result);
        Assert.Equal("", result!.Title);
        Assert.Equal("", result.Developer);
        Assert.Equal("", result.Year);
        Assert.Equal("", result.Languages);
        Assert.Equal("", result.Description);
    }

    [Fact]
    public void ApiError_ReturnNull()
    {
        var json = """{"header":{"success":"false","error":"Game not found"}}""";
        var result = ScreenScraperClient.ParseGameJson(json);
        Assert.Null(result);
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        var result = ScreenScraperClient.ParseGameJson("not valid json{{{");
        Assert.Null(result);
    }

    [Fact]
    public void Description_PrefersEnglish_OverOtherLanguages()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "dates": [], "langues": [], "medias": [],
              "synopsis": [
                { "langue": "fr", "text": "Version française" },
                { "langue": "en", "text": "English version" }
              ]
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.Equal("English version", result!.Description);
    }

    [Fact]
    public void Description_FallsBackToFirst_WhenNoEnglish()
    {
        var json = Wrap("""
            {
              "noms": [], "developpeur": { "text": "" }, "editeur": { "text": "" },
              "dates": [], "langues": [], "medias": [],
              "synopsis": [
                { "langue": "pt", "text": "Versão portuguesa" }
              ]
            }
            """);

        var result = ScreenScraperClient.ParseGameJson(json);

        Assert.Equal("Versão portuguesa", result!.Description);
    }
}
