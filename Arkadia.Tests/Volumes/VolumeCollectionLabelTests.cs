using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// The pure Volumes "COLLECTION" label formatter: Single owners render "&lt;Authority&gt; · &lt;Media&gt;" from
/// canonical display names with a graceful fallback chain; Group owners render the Group display name. This is a
/// presentation-only string — never an identity.
/// </summary>
public sealed class VolumeCollectionLabelTests
{
    // ── Single: authority + media (canonical casing preserved verbatim) ──

    [Theory]
    [InlineData("ReDump", "DVD",   "ReDump · DVD")]
    [InlineData("ReDump", "CD",    "ReDump · CD")]
    [InlineData("ReDump", "Other", "ReDump · Other")]
    [InlineData("TOSEC",  "Floppy","TOSEC · Floppy")]
    public void ForSingle_AuthorityAndMedia_JoinsWithMiddleDot(string authority, string media, string expected)
        => Assert.Equal(expected, VolumeCollectionLabel.ForSingle(authority, media, "some-name", "plat-auth-media"));

    [Fact]
    public void ForSingle_CasingIsPreservedVerbatim()
        // The formatter never re-cases: it echoes the canonical names it is given.
        => Assert.Equal("ReDump · DVD", VolumeCollectionLabel.ForSingle("ReDump", "DVD", "dvd", "ps2-redump-dvd"));

    // ── Single: fallback chain ──

    [Theory]
    [InlineData(null,  "DVD")]
    [InlineData("",    "DVD")]
    [InlineData("   ", "DVD")]
    public void ForSingle_AuthorityMissing_MediaOnly(string? authority, string expected)
        => Assert.Equal(expected, VolumeCollectionLabel.ForSingle(authority, "DVD", "dvd-name", "dl-id"));

    [Fact]
    public void ForSingle_AuthorityAndMediaUnresolvable_FallsBackToName()
        => Assert.Equal("Redump PS2 DVD", VolumeCollectionLabel.ForSingle(null, "", "Redump PS2 DVD", "ps2-redump-dvd-001"));

    [Fact]
    public void ForSingle_NameUnavailable_FallsBackToDatLineId()
        => Assert.Equal("ps2-redump-dvd-001", VolumeCollectionLabel.ForSingle(null, null, null, "ps2-redump-dvd-001"));

    [Fact]
    public void ForSingle_NameWhitespaceOnly_FallsBackToDatLineId()
        => Assert.Equal("dl-id", VolumeCollectionLabel.ForSingle("", "  ", "   ", "dl-id"));

    // ── Group: exact display name, defensive id fallback ──

    [Fact]
    public void ForGroup_UsesDisplayNameVerbatim()
        => Assert.Equal("Commodore 64 TOSEC", VolumeCollectionLabel.ForGroup("Commodore 64 TOSEC", "c64-tosec"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForGroup_MissingDisplayName_FallsBackToGroupId(string? display)
        => Assert.Equal("c64-tosec", VolumeCollectionLabel.ForGroup(display, "c64-tosec"));
}
