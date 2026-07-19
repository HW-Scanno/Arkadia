using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// Threshold → colour mapping for Systems wanted coverage. Pure UI policy (single source
/// of truth); asserts each band and the semantic split between 0% and N/A.
/// </summary>
public sealed class SystemsCoverageColorPolicyTests
{
    [Theory]
    [InlineData(100, SystemsCoverageColorPolicy.Cyan)]
    [InlineData(99,  SystemsCoverageColorPolicy.LightGreen)]
    [InlineData(80,  SystemsCoverageColorPolicy.LightGreen)]
    [InlineData(79,  SystemsCoverageColorPolicy.Green)]
    [InlineData(60,  SystemsCoverageColorPolicy.Green)]
    [InlineData(59,  SystemsCoverageColorPolicy.Yellow)]
    [InlineData(40,  SystemsCoverageColorPolicy.Yellow)]
    [InlineData(39,  SystemsCoverageColorPolicy.Orange)]
    [InlineData(20,  SystemsCoverageColorPolicy.Orange)]
    [InlineData(19,  SystemsCoverageColorPolicy.Red)]
    [InlineData(1,   SystemsCoverageColorPolicy.Red)]
    public void HexFor_MapsEachBand(int pct, string expectedHex)
        => Assert.Equal(expectedHex, SystemsCoverageColorPolicy.HexFor(pct));

    [Fact]
    public void HexFor_ZeroPercent_IsDarkBlue_NotGrey()
    {
        // 0% = wanted releases exist but none present → dark blue, NOT the neutral grey.
        Assert.Equal(SystemsCoverageColorPolicy.DarkBlue, SystemsCoverageColorPolicy.HexFor(0));
        Assert.NotEqual(SystemsCoverageColorPolicy.NeutralGray, SystemsCoverageColorPolicy.HexFor(0));
    }

    [Fact]
    public void HexFor_Null_IsNeutralGrey_ForNA()
    {
        // N/A = no wanted releases (all-unwanted system) → neutral grey.
        Assert.Equal(SystemsCoverageColorPolicy.NeutralGray, SystemsCoverageColorPolicy.HexFor(null));
    }

    [Fact]
    public void HexFor_ZeroAndNA_AreDistinct()
        => Assert.NotEqual(SystemsCoverageColorPolicy.HexFor(0), SystemsCoverageColorPolicy.HexFor(null));

    [Fact]
    public void AllBandColors_AreDistinctHexValues()
    {
        var colors = new[]
        {
            SystemsCoverageColorPolicy.Cyan, SystemsCoverageColorPolicy.LightGreen,
            SystemsCoverageColorPolicy.Green, SystemsCoverageColorPolicy.Yellow,
            SystemsCoverageColorPolicy.Orange, SystemsCoverageColorPolicy.Red,
            SystemsCoverageColorPolicy.DarkBlue, SystemsCoverageColorPolicy.NeutralGray,
        };
        Assert.Equal(colors.Length, new System.Collections.Generic.HashSet<string>(colors).Count);
    }
}
