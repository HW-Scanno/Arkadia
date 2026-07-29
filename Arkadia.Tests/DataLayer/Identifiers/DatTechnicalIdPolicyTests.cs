using System.Globalization;
using System.Threading;
using Arkadia.Data.Identifiers;
using Xunit;

namespace Arkadia.Tests.Data.Identifiers;

/// <summary>
/// Tests for the shared new-id policy: canonical validation, reserved names, length
/// target/hard-limit, and the pure suggestion normalizer. Pure — no DB, no filesystem.
/// </summary>
public sealed class DatTechnicalIdPolicyTests
{
    // ── Valid canonical ids ───────────────────────────────────────────────────

    [Theory]
    [InlineData("a")]
    [InlineData("tosec")]
    [InlineData("tosec-c64")]
    [InlineData("tosec-c64-games-disk")]
    [InlineData("abc123")]
    [InlineData("a1-b2-c3")]
    public void Validate_CanonicalIds_ReturnsNone(string id)
    {
        Assert.Equal(DatTechnicalIdError.None, DatTechnicalIdPolicy.Validate(id, out var warn));
        Assert.False(warn);
        Assert.True(DatTechnicalIdPolicy.IsValidNew(id));
    }

    // ── Empty ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_NullOrEmpty_ReturnsEmpty(string? id)
        => Assert.Equal(DatTechnicalIdError.Empty, DatTechnicalIdPolicy.Validate(id, out _));

    // ── Invalid syntax → NotCanonical ─────────────────────────────────────────

    [Theory]
    [InlineData("   ")]           // whitespace only
    [InlineData(" tosec")]        // leading whitespace
    [InlineData("tosec ")]        // trailing whitespace
    [InlineData("TOSEC")]         // uppercase
    [InlineData("Tosec-C64")]     // mixed case
    [InlineData("tosec_c64")]     // underscore
    [InlineData("tosec c64")]     // inner space
    [InlineData("tosec.c64")]     // dot
    [InlineData("tosec/c64")]     // slash
    [InlineData("tosec\\c64")]    // backslash
    [InlineData("..")]            // path traversal
    [InlineData("-tosec")]        // leading hyphen
    [InlineData("tosec-")]        // trailing hyphen
    [InlineData("tosec--c64")]    // double hyphen
    [InlineData("toséc")]         // accented
    [InlineData("日本語")]          // non-latin
    [InlineData("games💾")]        // emoji
    [InlineData("a\nb")]          // newline
    [InlineData("a\tb")]          // tab
    public void Validate_InvalidSyntax_ReturnsNotCanonical(string id)
        => Assert.Equal(DatTechnicalIdError.NotCanonical, DatTechnicalIdPolicy.Validate(id, out _));

    // ── Reserved names ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("con")]
    [InlineData("prn")]
    [InlineData("aux")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("com9")]
    [InlineData("lpt1")]
    [InlineData("lpt9")]
    public void Validate_ReservedNames_ReturnsReservedName(string id)
        => Assert.Equal(DatTechnicalIdError.ReservedName, DatTechnicalIdPolicy.Validate(id, out _));

    [Theory]
    [InlineData("CON")]
    [InlineData("Nul")]
    public void Validate_UppercaseReservedName_RejectedAsNotCanonical(string id)
    {
        // Uppercase fails the canonical-form check first (a rejection either way). The
        // case-insensitive reserved set still catches the lowercase canonical form ("con").
        Assert.Equal(DatTechnicalIdError.NotCanonical, DatTechnicalIdPolicy.Validate(id, out _));
    }

    [Theory]
    [InlineData("tosec-con")]
    [InlineData("con-tosec")]
    [InlineData("computer")]
    [InlineData("com10")]
    [InlineData("lpt10")]
    public void Validate_ReservedNameLookalikes_AreValid(string id)
        => Assert.Equal(DatTechnicalIdError.None, DatTechnicalIdPolicy.Validate(id, out _));

    // ── Target length / hard limit ────────────────────────────────────────────

    [Fact]
    public void Validate_TargetLength48_ValidNoWarning()
    {
        var id = new string('a', 48);
        Assert.Equal(DatTechnicalIdError.None, DatTechnicalIdPolicy.Validate(id, out var warn));
        Assert.False(warn);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(64)]
    public void Validate_BetweenTargetAndMax_ValidWithWarning(int length)
    {
        var id = new string('a', length);
        Assert.Equal(DatTechnicalIdError.None, DatTechnicalIdPolicy.Validate(id, out var warn));
        Assert.True(warn);
    }

    [Fact]
    public void Validate_Over64_ReturnsTooLong()
    {
        var id = new string('a', 65);
        Assert.Equal(DatTechnicalIdError.TooLong, DatTechnicalIdPolicy.Validate(id, out _));
    }

    // ── Suggestion normalization ──────────────────────────────────────────────

    [Theory]
    [InlineData(" Commodore 64 Games ", "commodore-64-games")]
    [InlineData("Games_[PRG]",          "games-prg")]
    [InlineData("Games///Disk",         "games-disk")]
    [InlineData("---Games---Disk---",   "games-disk")]
    [InlineData("Games...Disk",         "games-disk")]
    [InlineData("💾 Games",             "games")]
    public void NormalizeSuggestion_ProducesExpected(string input, string expected)
        => Assert.Equal(expected, DatTechnicalIdPolicy.NormalizeSuggestion(input));

    [Fact]
    public void NormalizeSuggestion_AccentedLatin_TransliteratesToAscii()
    {
        // Unicode FormD decomposition drops the accent, leaving a clean ASCII slug.
        Assert.Equal("applicazioni", DatTechnicalIdPolicy.NormalizeSuggestion("Àpplicazioni"));
    }

    [Fact]
    public void NormalizeSuggestion_NonLatinScript_ReturnsEmpty()
    {
        // No representable ASCII alphanumerics → empty (caller must handle).
        Assert.Equal("", DatTechnicalIdPolicy.NormalizeSuggestion("日本語"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeSuggestion_NullOrEmpty_ReturnsEmpty(string? input)
        => Assert.Equal("", DatTechnicalIdPolicy.NormalizeSuggestion(input));

    // ── Purity: culture-independence (Turkish dotted/undotted I) ──────────────

    [Fact]
    public void Policy_IsCultureInvariant_UnderTurkishCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");

            // "I" must fold to ASCII "i" regardless of the Turkish dotless-i rule.
            Assert.Equal("igames", DatTechnicalIdPolicy.NormalizeSuggestion("IGames"));

            // Canonical validation is defined on [a-z0-9] and unaffected by culture.
            Assert.Equal(DatTechnicalIdError.None, DatTechnicalIdPolicy.Validate("i-games", out _));
            Assert.Equal(DatTechnicalIdError.NotCanonical, DatTechnicalIdPolicy.Validate("I-games", out _));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
