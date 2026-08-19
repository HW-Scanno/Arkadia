using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>The PO-authoritative completion rule: Wanted = max(0, releases − unwanted); percent = present·100/wanted
/// (integer); null ("N/A") when wanted == 0. Shared by System, Single DAT, Group and Group-leaf scopes.</summary>
public sealed class CompletionMathTests
{
    [Theory]
    [InlineData(100, 0, 100)]   // no unwanted → wanted = releases
    [InlineData(100, 10, 90)]   // unwanted excluded from the denominator
    [InlineData(100, 200, 0)]   // never negative
    public void Wanted_ExcludesUnwanted(int releases, int unwanted, int expected)
        => Assert.Equal(expected, CompletionMath.Wanted(releases, unwanted));

    [Fact] // wanted denominator excludes unwanted: 5 present of (30 − 2) = 28 → 17%
    public void Percent_ExcludesUnwantedFromDenominator()
        => Assert.Equal(17, CompletionMath.Percent(present: 5, releases: 30, unwanted: 2));

    [Fact] // integer division (not rounded): 1 of 3 → 33%
    public void Percent_IsIntegerDivision()
        => Assert.Equal(33, CompletionMath.Percent(1, 3, 0));

    [Fact] // Wanted == 0 → null (N/A), never 0%
    public void Percent_ZeroWanted_IsNull()
        => Assert.Null(CompletionMath.Percent(present: 0, releases: 5, unwanted: 5));

    [Fact] // wanted releases exist but none present → 0% (distinct from N/A)
    public void Percent_NonePresent_IsZeroNotNull()
        => Assert.Equal(0, CompletionMath.Percent(present: 0, releases: 5, unwanted: 0));

    [Fact]
    public void Text_NullIsNa()
    {
        Assert.Equal("N/A", CompletionMath.Text(null));
        Assert.Equal("94%", CompletionMath.Text(94));
    }

    [Theory] // IsIncomplete = has wanted releases AND present < wanted (completion only)
    [InlineData(5, 10, 0, true)]    // 5/10 wanted → incomplete
    [InlineData(10, 10, 0, false)]  // 10/10 → complete
    [InlineData(0, 5, 5, false)]    // all unwanted → N/A → not incomplete (nothing wanted to complete)
    [InlineData(28, 30, 2, false)]  // 28/28 wanted → complete even though releases=30
    public void IsIncomplete_CompletionOnly(int present, int releases, int unwanted, bool expected)
        => Assert.Equal(expected, CompletionMath.IsIncomplete(present, releases, unwanted));
}
