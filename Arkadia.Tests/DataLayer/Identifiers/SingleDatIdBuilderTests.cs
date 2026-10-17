using System;
using System.Linq;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Xunit;

namespace Arkadia.Tests.DataLayer.Identifiers;

/// <summary>DAT-NAMING-1: pure Single DAT id builder/validator and the legacy display-name fallback.</summary>
public sealed class SingleDatIdBuilderTests
{
    private static readonly string[] None = Array.Empty<string>();

    private static SingleDatIdResult B(string? fam, string? auth, string? media, string? variant, params string[] existing)
        => SingleDatIdBuilder.Build(fam, auth, media, variant, existing);

    // ── Valid construction ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("nds", "nointro", "cartridge", "main",         "nds-nointro-cartridge-main")]
    [InlineData("nds", "nointro", "other",     "downloadplay", "nds-nointro-other-downloadplay")]
    [InlineData("psx", "local",   "other",     "sbi",          "psx-local-other-sbi")]
    [InlineData("c64", "custom",  "disk",      "gamebase",     "c64-custom-disk-gamebase")]
    public void Build_ComposesFourComponentsFromTheSelectedValues(string fam, string auth, string media, string variant, string expected)
    {
        var r = B(fam, auth, media, variant);
        Assert.True(r.IsValid);
        Assert.Equal(expected, r.Id);
        Assert.Equal(SingleDatIdError.None, r.Error);
    }

    [Fact]
    public void Build_DerivesFirstThreeComponentsFromInputs_NotFromConstants()
    {
        var r = B("zzz", "qqq", "www", "v1");
        Assert.Equal("zzz-qqq-www-v1", r.Id);
    }

    [Fact]
    public void Build_AcceptsHyphenatedSystemAuthorityMediaAndVariant()
    {
        var r = B("game-boy", "no-intro", "rom-pack", "download-play");
        Assert.True(r.IsValid);
        Assert.Equal("game-boy-no-intro-rom-pack-download-play", r.Id);
    }

    [Fact]
    public void Build_ProducesAnIdThatSatisfiesTheSharedPolicy()
    {
        var r = B("nds", "nointro", "cartridge", "main");
        Assert.True(DatTechnicalIdPolicy.IsValidNew(r.Id));
    }

    [Fact]
    public void Build_DisplayNameIsNotAnInput_IdIsStable()
    {
        // The signature has no display-name parameter: the same structured values always give the same id.
        Assert.Equal(B("nds", "nointro", "cartridge", "main").Id, B("nds", "nointro", "cartridge", "main").Id);
    }

    // ── Variant suggestion ──────────────────────────────────────────────────────

    [Fact]
    public void DefaultVariant_IsMain() => Assert.Equal("main", SingleDatIdBuilder.DefaultVariant);

    [Theory]
    [InlineData(null,            "main")]
    [InlineData("",              "main")]
    [InlineData("   ",           "main")]
    [InlineData("---",           "main")]
    [InlineData("日本語",          "main")]
    [InlineData("Download Play", "download-play")]
    [InlineData("  SBI  ",       "sbi")]
    [InlineData("Game_Base!",    "game-base")]
    [InlineData("Café",          "cafe")]
    public void SuggestVariant_NormalizesOrFallsBackToMain_AndIsAlwaysValid(string? input, string expected)
    {
        var s = SingleDatIdBuilder.SuggestVariant(input);
        Assert.Equal(expected, s);
        Assert.True(SingleDatIdBuilder.IsValidVariant(s));
    }

    // ── Invalid variants: rejected, never rewritten ─────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Build_EmptyVariant_IsRejected(string? variant)
    {
        var r = B("nds", "nointro", "cartridge", variant);
        Assert.False(r.IsValid);
        Assert.Null(r.Id);
        Assert.Equal(SingleDatIdError.VariantEmpty, r.Error);
    }

    [Theory]
    [InlineData("Main")]                 // uppercase
    [InlineData("MAIN")]
    [InlineData(" main")]                // leading space — not trimmed
    [InlineData("main ")]                // trailing space
    [InlineData("download play")]        // space
    [InlineData("download_play")]        // underscore
    [InlineData("-main")]                // leading hyphen
    [InlineData("main-")]                // trailing hyphen
    [InlineData("ma--in")]               // double hyphen
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("main\n")]               // '$' would accept this; \z must not
    [InlineData("main\r\n")]
    [InlineData("ma\tin")]
    [InlineData("main.dat")]             // dot
    [InlineData("..")]                   // traversal
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("c:")]
    [InlineData("con:")]
    [InlineData("main?")]
    [InlineData("ma*in")]
    [InlineData("ma<in")]
    [InlineData("ma\"in")]
    [InlineData("ma|in")]
    [InlineData("café")]                 // non-ASCII
    [InlineData("мain")]                 // Cyrillic homoglyph
    [InlineData("ｍain")]                 // fullwidth
    [InlineData("main\0")]
    public void Build_InvalidVariant_IsRejectedAndNeverRewritten(string variant)
    {
        var r = B("nds", "nointro", "cartridge", variant);
        Assert.False(r.IsValid);
        Assert.Null(r.Id);
        Assert.Equal(SingleDatIdError.VariantInvalid, r.Error);
        Assert.False(string.IsNullOrWhiteSpace(r.Message));
        Assert.False(SingleDatIdBuilder.IsValidVariant(variant));
    }

    [Fact]
    public void InvalidVariantMessage_NamesTheOffendingValue()
    {
        var r = B("nds", "nointro", "cartridge", "Bad Value");
        Assert.Contains("Bad Value", r.Message);
    }

    // ── Invalid structured components ───────────────────────────────────────────

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("NDS")] [InlineData("n ds")] [InlineData("n_ds")]
    [InlineData("..")] [InlineData("a/b")] [InlineData("nds-")] [InlineData("-nds")]
    public void Build_InvalidSystem_IsRejected(string? fam)
        => Assert.Equal(SingleDatIdError.InvalidSystem, B(fam, "nointro", "cartridge", "main").Error);

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("NoIntro")] [InlineData("no intro")] [InlineData("..")]
    public void Build_InvalidAuthority_IsRejected(string? auth)
        => Assert.Equal(SingleDatIdError.InvalidAuthority, B("nds", auth, "cartridge", "main").Error);

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Cartridge")] [InlineData("car tridge")] [InlineData("a\\b")]
    public void Build_InvalidMediaType_IsRejected(string? media)
        => Assert.Equal(SingleDatIdError.InvalidMediaType, B("nds", "nointro", media, "main").Error);

    // ── Length / reserved ───────────────────────────────────────────────────────

    [Fact]
    public void Build_IdAtMaxLength_IsAccepted_AndFlaggedWhenAboveRecommended()
    {
        var prefix  = "nds-nointro-cartridge-";                       // 22 chars
        var variant = new string('a', DatTechnicalIdPolicy.MaxLength - prefix.Length);
        var r = B("nds", "nointro", "cartridge", variant);
        Assert.True(r.IsValid);
        Assert.Equal(DatTechnicalIdPolicy.MaxLength, r.Id!.Length);
        Assert.True(r.ExceedsRecommendedLength);
    }

    [Fact]
    public void Build_ShortId_IsNotFlaggedAsLong()
        => Assert.False(B("nds", "nointro", "cartridge", "main").ExceedsRecommendedLength);

    [Fact]
    public void Build_IdOverMaxLength_IsRejected()
    {
        var variant = new string('a', DatTechnicalIdPolicy.MaxLength);
        var r = B("nds", "nointro", "cartridge", variant);
        Assert.False(r.IsValid);
        Assert.Equal(SingleDatIdError.IdTooLong, r.Error);
    }

    // ── Collisions (case-insensitive, all existing ids) ─────────────────────────

    [Fact]
    public void Build_ExactCollision_IsRejected()
    {
        var r = B("nds", "nointro", "cartridge", "main", "nds-nointro-cartridge-main");
        Assert.False(r.IsValid);
        Assert.Equal(SingleDatIdError.Collision, r.Error);
        Assert.Contains("nds-nointro-cartridge-main", r.Message);
    }

    [Theory]
    [InlineData("NDS-NOINTRO-CARTRIDGE-MAIN")]
    [InlineData("Nds-NoIntro-Cartridge-Main")]
    public void Build_CaseInsensitiveCollision_IsRejected(string existing)
        => Assert.Equal(SingleDatIdError.Collision, B("nds", "nointro", "cartridge", "main", existing).Error);

    [Fact]
    public void Build_CollisionWithGroupLeafId_IsRejected()
        => Assert.Equal(SingleDatIdError.Collision,
            B("c64", "tosec", "disk", "games", "c64-tosec-applications", "c64-tosec-disk-games").Error);

    [Fact]
    public void Build_NoCollisionWithUnrelatedIds()
        => Assert.True(B("nds", "nointro", "cartridge", "main", "nds-nointro-other-main", "gba-nointro-cartridge-main").IsValid);

    [Fact]
    public void Build_NullEntryInExistingIds_IsIgnored()
        => Assert.True(SingleDatIdBuilder.Build("nds", "nointro", "cartridge", "main", new string[] { null! }).IsValid);

    [Fact]
    public void Build_NullExistingIds_Throws()
        => Assert.Throws<ArgumentNullException>(() => SingleDatIdBuilder.Build("nds", "nointro", "cartridge", "main", null!));

    // ── Legacy compatibility ────────────────────────────────────────────────────

    [Fact]
    public void LegacyThreeComponentId_DoesNotCollideWithTheNewFourComponentId()
    {
        // Legacy nds-nointro-cartridge stays valid and untouched; the new default sits beside it.
        var r = B("nds", "nointro", "cartridge", "main", "nds-nointro-cartridge");
        Assert.True(r.IsValid);
        Assert.Equal("nds-nointro-cartridge-main", r.Id);
    }

    [Fact]
    public void LegacyMixedCaseId_StillLoadsVerbatim_AndIsNotRejectedByTheBuilder()
    {
        // The builder only validates NEW ids; persisted legacy values go through FromPersisted untouched.
        var legacy = DatLineId.FromPersisted("NDS_NoIntro_Cartridge");
        Assert.Equal("NDS_NoIntro_Cartridge", legacy.Value);
        Assert.False(legacy.ConformsToNewPolicy);
        Assert.True(B("nds", "nointro", "cartridge", "main", legacy.Value).IsValid);
    }

    [Fact]
    public void Build_DoesNotParseExistingIds_ItOnlyComparesThem()
    {
        // Odd legacy ids must never throw or influence anything except an exact (case-insensitive) match.
        var odd = new[] { "", "-", "a--b", "x y", "../..", "nds-nointro-cartridge-main-extra" };
        Assert.True(B("nds", "nointro", "cartridge", "main", odd).IsValid);
    }

    // ── DatLineDisplayName ──────────────────────────────────────────────────────

    [Fact]
    public void DisplayName_Null_FallsBackToTheLegacyLabel()
        => Assert.Equal("No-Intro · CARTRIDGE", DatLineDisplayName.Resolve(null, "No-Intro", "cartridge"));

    [Theory]
    [InlineData("")] [InlineData("   ")]
    public void DisplayName_Blank_FallsBackToTheLegacyLabel(string blank)
        => Assert.Equal("ReDump · DVD", DatLineDisplayName.Resolve(blank, "ReDump", "dvd"));

    [Fact]
    public void DisplayName_Stored_Wins_AndIsReturnedVerbatim()
        => Assert.Equal("NoIntro - Main", DatLineDisplayName.Resolve("NoIntro - Main", "No-Intro", "cartridge"));

    [Fact]
    public void DisplayName_LegacyFormat_MatchesMainWindowFormatDatLineName()
        // MainWindow.FormatDatLineName: $"{GetAuthorityName(id)} · {mediaTypeId.ToUpperInvariant()}".
        => Assert.Equal("TOSEC · FLOPPY", DatLineDisplayName.Legacy("TOSEC", "floppy"));

    [Fact]
    public void AllValidVariantsProduceDistinctIdsPerVariant()
    {
        var ids = new[] { "main", "sbi", "gamebase" }.Select(v => B("psx", "local", "other", v).Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
