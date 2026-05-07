using Arkadia;
using Xunit;

namespace Arkadia.Tests;

public sealed class CacheReviewDialogTests
{
    // ── Auto-select logic ─────────────────────────────────────────────────────

    [Fact]
    public void ShouldAutoSelect_OneCandidate_ReturnsTrue()
        => Assert.True(CacheReviewDialog.ShouldAutoSelect(1));

    [Fact]
    public void ShouldAutoSelect_ZeroCandidates_ReturnsFalse()
        => Assert.False(CacheReviewDialog.ShouldAutoSelect(0));

    [Fact]
    public void ShouldAutoSelect_TwoCandidates_ReturnsFalse()
        => Assert.False(CacheReviewDialog.ShouldAutoSelect(2));

    [Fact]
    public void ShouldAutoSelect_ManyCandidates_ReturnsFalse()
        => Assert.False(CacheReviewDialog.ShouldAutoSelect(10));
}
