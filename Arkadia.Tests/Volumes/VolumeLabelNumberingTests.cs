using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>Shared volume-label auto-numbering (Single + Group Create). Pure, global namespace, gaps skipped.</summary>
public sealed class VolumeLabelNumberingTests
{
    [Fact]
    public void NextNumber_NoExisting_IsOne()
        => Assert.Equal(1, VolumeLabelNumbering.NextNumber(new string[0], "c64-tosec"));

    [Fact] // 001, 003 present → next is 004 (max+1; gap at 002 is NOT refilled)
    public void NextNumber_SkipsGaps_MaxPlusOne()
        => Assert.Equal(4, VolumeLabelNumbering.NextNumber(
            new[] { "c64-tosec-001", "c64-tosec-003" }, "c64-tosec"));

    [Fact] // prefix match is case-insensitive
    public void NextNumber_CaseInsensitivePrefix()
        => Assert.Equal(3, VolumeLabelNumbering.NextNumber(
            new[] { "C64-TOSEC-002" }, "c64-tosec"));

    [Fact] // Group and Single prefixes are naturally disjoint (int-tail parse rejects the other's suffix)
    public void NextNumber_GroupAndSingleDisjoint()
    {
        var labels = new[] { "c64-tosec-001", "c64-tosec-games-strategy-d64-001", "c64-tosec-games-strategy-d64-002" };
        Assert.Equal(2, VolumeLabelNumbering.NextNumber(labels, "c64-tosec"));                         // only c64-tosec-001
        Assert.Equal(3, VolumeLabelNumbering.NextNumber(labels, "c64-tosec-games-strategy-d64"));      // the two leaf labels
    }

    [Fact]
    public void SuggestLabel_FormatsThreeDigits()
    {
        Assert.Equal("c64-tosec-001", VolumeLabelNumbering.SuggestLabel(new string[0], "c64-tosec"));
        Assert.Equal("c64-tosec-004", VolumeLabelNumbering.SuggestLabel(
            new[] { "c64-tosec-001", "c64-tosec-003" }, "c64-tosec"));
    }
}
