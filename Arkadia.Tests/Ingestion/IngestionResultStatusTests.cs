using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

public sealed class IngestionResultStatusTests
{
    // ── IngestionResult_IncompleteOnly ─────────────────────────────────────────

    [Fact]
    public void IncompleteOnly_NoReleasesPresent_StatusIsFailed()
    {
        var r = new IngestionResult
        {
            TransformsFailed   = 0,
            ReleasesIncomplete = 1,
            ReleasesPresent    = 0,
        };
        Assert.Equal(IngestionStatus.Failed, r.Status);
        Assert.Equal("FAILED", r.StatusText);
        Assert.False(r.Success);
    }

    // ── IngestionResult_IncompleteWithSuccess ──────────────────────────────────

    [Fact]
    public void IncompleteWithSuccessfulRelease_StatusIsPartialSuccess()
    {
        var r = new IngestionResult
        {
            TransformsFailed   = 0,
            ReleasesIncomplete = 1,
            ReleasesPresent    = 1,
        };
        Assert.Equal(IngestionStatus.PartialSuccess, r.Status);
        Assert.Equal("PARTIAL SUCCESS", r.StatusText);
        Assert.False(r.Success);
    }

    // ── IngestionResult_NoIncompleteNoFailure ──────────────────────────────────

    [Fact]
    public void NoIncompleteNoTransformFailure_StatusIsSuccess()
    {
        var r = new IngestionResult
        {
            TransformsFailed   = 0,
            ReleasesIncomplete = 0,
            ReleasesPresent    = 1,
        };
        Assert.Equal(IngestionStatus.Success, r.Status);
        Assert.Equal("SUCCESS", r.StatusText);
        Assert.True(r.Success);
    }

    // ── IngestionResult_FatalError ─────────────────────────────────────────────

    [Fact]
    public void FatalError_OverridesIncomplete_StatusIsFailed()
    {
        var r = new IngestionResult
        {
            Error              = "disk full",
            TransformsFailed   = 0,
            ReleasesIncomplete = 0,
            ReleasesPresent    = 1,
        };
        Assert.Equal(IngestionStatus.Failed, r.Status);
        Assert.Equal("FAILED", r.StatusText);
        Assert.False(r.Success);
    }

    // ── IngestionResult_TransformFailOnly ────────────────────────────────────

    [Fact]
    public void TransformFailOnly_NoReleasesPresent_StatusIsFailed()
    {
        var r = new IngestionResult
        {
            TransformsFailed   = 1,
            ReleasesIncomplete = 0,
            ReleasesPresent    = 0,
        };
        Assert.Equal(IngestionStatus.Failed, r.Status);
    }

    [Fact]
    public void TransformFailWithOtherSuccess_StatusIsPartialSuccess()
    {
        var r = new IngestionResult
        {
            TransformsFailed   = 1,
            ReleasesIncomplete = 0,
            ReleasesPresent    = 2,
        };
        Assert.Equal(IngestionStatus.PartialSuccess, r.Status);
    }
}
