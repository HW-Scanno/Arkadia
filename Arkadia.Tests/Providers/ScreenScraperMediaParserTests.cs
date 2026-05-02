using Arkadia.Providers;
using Xunit;

namespace Arkadia.Tests.Providers;

/// <summary>
/// Tests for ScreenScraperClient.ParseGameJson — media collection parsing:
/// screenshots, fanart, video preference, logos, covers (region filtering),
/// marquees, flyers, and manuals.
/// </summary>
public sealed class ScreenScraperMediaParserTests
{
    private static string Wrap(string jeuJson)
        => "{\"response\":{\"jeu\":" + jeuJson + "}}";

    private static string MediaItem(string type, string url, string fmt, string? region = null)
    {
        var regionPart = region is not null ? $",\"region\":\"{region}\"" : "";
        return $"{{\"type\":\"{type}\",\"url\":\"{url}\",\"format\":\"{fmt}\"{regionPart}}}";
    }

    private static string JeuWithMedias(params string[] items)
        => $$"""
             {
               "noms":[],"developpeur":{"text":""},"editeur":{"text":""},
               "dates":[],"synopsis":[],"langues":[],
               "medias":[{{string.Join(",", items)}}]
             }
             """;

    // ── Title screenshots ─────────────────────────────────────────────────────

    [Fact]
    public void TitleScreenshots_CollectsAllSstitle()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("sstitle", "http://a/1.php", "png"),
            MediaItem("sstitle", "http://a/2.php", "jpg"),
            MediaItem("ss",      "http://a/3.php", "png")  // different type — excluded
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Equal(2, r.TitleScreenshots.Count);
        Assert.Equal("http://a/1.php", r.TitleScreenshots[0].Url);
        Assert.Equal("png",            r.TitleScreenshots[0].Format);
        Assert.Equal("http://a/2.php", r.TitleScreenshots[1].Url);
    }

    [Fact]
    public void TitleScreenshots_EmptyWhenNonePresent()
    {
        var json = Wrap(JeuWithMedias(MediaItem("ss", "http://a/1.php", "png")));
        Assert.Empty(ScreenScraperClient.ParseGameJson(json)!.TitleScreenshots);
    }

    // ── Gameplay screenshots ──────────────────────────────────────────────────

    [Fact]
    public void GameplayScreenshots_CollectsAllSs()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("ss",      "http://a/1.php", "png"),
            MediaItem("ss",      "http://a/2.php", "jpg"),
            MediaItem("sstitle", "http://a/3.php", "png")  // different type — excluded
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Equal(2, r.GameplayScreenshots.Count);
        Assert.Equal("http://a/1.php", r.GameplayScreenshots[0].Url);
    }

    // ── Fanart ────────────────────────────────────────────────────────────────

    [Fact]
    public void Fanart_CollectsAll()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("fanart", "http://a/f1.php", "jpg"),
            MediaItem("fanart", "http://a/f2.php", "png")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Equal(2, r.Fanart.Count);
        Assert.Equal("http://a/f1.php", r.Fanart[0].Url);
        Assert.Equal("jpg",             r.Fanart[0].Format);
    }

    // ── Video preference ──────────────────────────────────────────────────────

    [Fact]
    public void Video_PrefersNormalized_OverStandard()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("video",            "http://a/std.php",  "mp4"),
            MediaItem("video-normalized", "http://a/norm.php", "mp4")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.NotNull(r.Video);
        Assert.Equal("http://a/norm.php", r.Video!.Url);
    }

    [Fact]
    public void Video_FallsBackToStandard_WhenNoNormalized()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("video", "http://a/std.php", "mp4")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.NotNull(r.Video);
        Assert.Equal("http://a/std.php", r.Video!.Url);
        Assert.Equal("mp4",              r.Video.Format);
    }

    [Fact]
    public void Video_IsNull_WhenNoVideo()
    {
        var json = Wrap(JeuWithMedias(MediaItem("ss", "http://a/1.php", "png")));
        Assert.Null(ScreenScraperClient.ParseGameJson(json)!.Video);
    }

    // ── Logos ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LogosHd_CollectsWheelHd()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("wheel-hd", "http://a/hd.php",  "png"),
            MediaItem("wheel",    "http://a/std.php",  "png")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.LogosHd);
        Assert.Equal("http://a/hd.php", r.LogosHd[0].Url);
    }

    [Fact]
    public void Logos_CollectsWheel()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("wheel-hd", "http://a/hd.php",  "png"),
            MediaItem("wheel",    "http://a/std.php",  "png")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.Logos);
        Assert.Equal("http://a/std.php", r.Logos[0].Url);
    }

    [Fact]
    public void Logos_IgnoresWheelCarbon_And_WheelSteel()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("wheel-carbon", "http://a/c.php", "png"),
            MediaItem("wheel-steel",  "http://a/s.php", "png")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Empty(r.LogosHd);
        Assert.Empty(r.Logos);
    }

    // ── Marquees ──────────────────────────────────────────────────────────────

    [Fact]
    public void Marquees_CollectsAll()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("marquee", "http://a/m1.php", "png"),
            MediaItem("marquee", "http://a/m2.php", "png")
        ));

        Assert.Equal(2, ScreenScraperClient.ParseGameJson(json)!.Marquees.Count);
    }

    // ── Flyers ────────────────────────────────────────────────────────────────

    [Fact]
    public void Flyers_CollectsAll()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("flyer", "http://a/fl1.php", "jpg"),
            MediaItem("flyer", "http://a/fl2.php", "jpg")
        ));

        Assert.Equal(2, ScreenScraperClient.ParseGameJson(json)!.Flyers.Count);
    }

    // ── Manuals ───────────────────────────────────────────────────────────────

    [Fact]
    public void Manuals_CollectsAll()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("manuel", "http://a/man.php", "pdf")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.Manuals);
        Assert.Equal("http://a/man.php", r.Manuals[0].Url);
        Assert.Equal("pdf",              r.Manuals[0].Format);
    }

    // ── Cover front — region collection and filtering ─────────────────────────

    [Fact]
    public void CoverFront_CollectsAllRegions()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("box-2D", "http://a/wor.php", "jpg", "wor"),
            MediaItem("box-2D", "http://a/us.php",  "jpg", "us"),
            MediaItem("box-2D", "http://a/eu.php",  "jpg", "eu"),
            MediaItem("box-2D", "http://a/jp.php",  "jpg", "jp")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Equal(4, r.CoverFront.Count);
        Assert.Equal("wor", r.CoverFront[0].Region);
        Assert.Equal("us",  r.CoverFront[1].Region);
        Assert.Equal("eu",  r.CoverFront[2].Region);
        Assert.Equal("jp",  r.CoverFront[3].Region);
    }

    [Fact]
    public void CoverFront_ExcludesCustomRegion()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("box-2D", "http://a/wor.php",    "jpg", "wor"),
            MediaItem("box-2D", "http://a/custom.php", "jpg", "custom")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.CoverFront);
        Assert.Equal("wor", r.CoverFront[0].Region);
    }

    [Fact]
    public void CoverFront_ExcludesPersonalizedRegion()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("box-2D", "http://a/eu.php",           "jpg", "eu"),
            MediaItem("box-2D", "http://a/personalized.php", "jpg", "personalized")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.CoverFront);
        Assert.Equal("eu", r.CoverFront[0].Region);
    }

    [Fact]
    public void CoverFront_ExcludesSsRegion()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("box-2D", "http://a/us.php", "jpg", "us"),
            MediaItem("box-2D", "http://a/ss.php", "jpg", "ss")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.CoverFront);
        Assert.Equal("us", r.CoverFront[0].Region);
    }

    [Fact]
    public void CoverFront_ExcludesScreenscraperRegion()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("box-2D", "http://a/jp.php",           "jpg", "jp"),
            MediaItem("box-2D", "http://a/screenscraper.php","jpg", "screenscraper")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.CoverFront);
        Assert.Equal("jp", r.CoverFront[0].Region);
    }

    // ── Cover back, spine, wrap ───────────────────────────────────────────────

    [Fact]
    public void CoverBack_CollectsBox2DBack()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("box-2D-back", "http://a/back.php", "jpg", "wor")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.CoverBack);
        Assert.Equal("http://a/back.php", r.CoverBack[0].Url);
        Assert.Equal("wor",               r.CoverBack[0].Region);
    }

    [Fact]
    public void CoverSpine_CollectsBox2DSide()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("box-2D-side", "http://a/side.php", "jpg", "eu")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.CoverSpine);
        Assert.Equal("http://a/side.php", r.CoverSpine[0].Url);
        Assert.Equal("eu",                r.CoverSpine[0].Region);
    }

    [Fact]
    public void CoverWrap_CollectsBoxTexture()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("box-texture", "http://a/tex.php", "jpg", "us")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.CoverWrap);
        Assert.Equal("http://a/tex.php", r.CoverWrap[0].Url);
        Assert.Equal("us",               r.CoverWrap[0].Region);
    }

    // ── Empty medias array ────────────────────────────────────────────────────

    [Fact]
    public void AllCollections_EmptyWhenMediasAbsent()
    {
        var json = Wrap("""
            {"noms":[],"developpeur":{"text":""},"editeur":{"text":""},
             "dates":[],"synopsis":[],"langues":[]}
            """);

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Empty(r.TitleScreenshots);
        Assert.Empty(r.GameplayScreenshots);
        Assert.Empty(r.Fanart);
        Assert.Empty(r.LogosHd);
        Assert.Empty(r.Logos);
        Assert.Empty(r.Marquees);
        Assert.Empty(r.Flyers);
        Assert.Empty(r.Manuals);
        Assert.Empty(r.CoverFront);
        Assert.Empty(r.CoverBack);
        Assert.Empty(r.CoverSpine);
        Assert.Empty(r.CoverWrap);
        Assert.Null(r.Video);
        Assert.Empty(r.PhysicalMedia);
        Assert.Empty(r.PhysicalTexture);
    }

    // ── Physical media ────────────────────────────────────────────────────────

    [Fact]
    public void PhysicalMedia_CollectsSupport2D()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("support-2D", "http://a/disc.php",  "png"),
            MediaItem("support-2D", "http://a/disc2.php", "png")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Equal(2, r.PhysicalMedia.Count);
        Assert.Equal("http://a/disc.php", r.PhysicalMedia[0].Url);
        Assert.Equal("png",               r.PhysicalMedia[0].Format);
    }

    [Fact]
    public void PhysicalTexture_CollectsSupportTexture()
    {
        var json = Wrap(JeuWithMedias(
            MediaItem("support-texture", "http://a/tex.php", "png")
        ));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.PhysicalTexture);
        Assert.Equal("http://a/tex.php", r.PhysicalTexture[0].Url);
    }

    [Fact]
    public void PhysicalMedia_EmptyWhenAbsent()
    {
        var json = Wrap(JeuWithMedias(MediaItem("ss", "http://a/1.php", "png")));

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Empty(r.PhysicalMedia);
        Assert.Empty(r.PhysicalTexture);
    }

    // ── Format default fallback ───────────────────────────────────────────────

    [Fact]
    public void MediaItem_UsesDefaultFormat_WhenFormatFieldAbsent()
    {
        // No "format" field in the JSON — should use the type default
        var json = Wrap("""
            {
              "noms":[],"developpeur":{"text":""},"editeur":{"text":""},
              "dates":[],"synopsis":[],"langues":[],
              "medias":[{"type":"ss","url":"http://a/1.php"}]
            }
            """);

        var r = ScreenScraperClient.ParseGameJson(json)!;

        Assert.Single(r.GameplayScreenshots);
        Assert.Equal("png", r.GameplayScreenshots[0].Format);
    }
}
