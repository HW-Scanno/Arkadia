using Arkadia;
using Xunit;

namespace Arkadia.Tests;

public class CatalogHeroHelpersTests
{
    // ── FormatGenreLabel ──────────────────────────────────────────────────────

    [Fact]
    public void FormatGenreLabel_BothPresent_CombinesWithSlash()
    {
        var result = CatalogHeroHelpers.FormatGenreLabel("Action", "Shooter");
        Assert.Equal("Genre: Action / Shooter", result);
    }

    [Fact]
    public void FormatGenreLabel_GenreOnly_ShowsGenre()
    {
        var result = CatalogHeroHelpers.FormatGenreLabel("RPG", "");
        Assert.Equal("Genre: RPG", result);
    }

    [Fact]
    public void FormatGenreLabel_SubgenreOnly_ShowsSubgenre()
    {
        var result = CatalogHeroHelpers.FormatGenreLabel("", "Beat em up");
        Assert.Equal("Genre: Beat em up", result);
    }

    [Fact]
    public void FormatGenreLabel_BothEmpty_ReturnsEmpty()
    {
        var result = CatalogHeroHelpers.FormatGenreLabel("", "");
        Assert.Equal("", result);
    }

    // ── ShouldShowOriginalTitle ───────────────────────────────────────────────

    [Fact]
    public void ShouldShowOriginalTitle_SameAsDisplayTitle_ReturnsFalse()
    {
        Assert.False(CatalogHeroHelpers.ShouldShowOriginalTitle(
            "Sega Bass Fishing", "Sega Bass Fishing", "sega_bass_fishing.zip"));
    }

    [Fact]
    public void ShouldShowOriginalTitle_SameAsDisplayTitle_CaseInsensitive_ReturnsFalse()
    {
        Assert.False(CatalogHeroHelpers.ShouldShowOriginalTitle(
            "SEGA BASS FISHING", "Sega Bass Fishing", "sega_bass_fishing.zip"));
    }

    [Fact]
    public void ShouldShowOriginalTitle_SameAsEntryName_ReturnsFalse()
    {
        Assert.False(CatalogHeroHelpers.ShouldShowOriginalTitle(
            "sega_bass_fishing.zip", "Sega Bass Fishing", "sega_bass_fishing.zip"));
    }

    [Fact]
    public void ShouldShowOriginalTitle_Empty_ReturnsFalse()
    {
        Assert.False(CatalogHeroHelpers.ShouldShowOriginalTitle(
            "", "Sega Bass Fishing", "sega_bass_fishing.zip"));
    }

    [Fact]
    public void ShouldShowOriginalTitle_DifferentFromBoth_ReturnsTrue()
    {
        Assert.True(CatalogHeroHelpers.ShouldShowOriginalTitle(
            "Sega Bass Fishing Challenge",
            "Sega Bass Fishing",
            "sega_bass_fishing.zip"));
    }
}
