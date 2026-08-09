using System;
using Arkadia.GroupDats;
using Xunit;

namespace Arkadia.Tests.GroupDats;

/// <summary>
/// Tests for the pure M4 wiring helpers (<see cref="GroupDatCreatePresenter"/>): the execute gate, the
/// result→presentation mapping, and the refresh-only-on-committed rule. These are the UI decisions the
/// MainWindow handler delegates to, extracted so they are testable without any Avalonia surface.
/// </summary>
public sealed class GroupDatCreatePresenterTests
{
    private static GroupDatExecutionResult Result(
        GroupDatExecutionStatus status,
        GroupDatExecutionErrorCode error = GroupDatExecutionErrorCode.None,
        string? message = null, string[]? cleanup = null, int published = 0)
        => new()
        {
            GroupId = "c64-tosec", OverallStatus = status, LeafTotal = 410, PublishedCount = published,
            ErrorCode = error, ErrorMessage = message, CleanupPaths = cleanup ?? Array.Empty<string>(),
        };

    // ── WillExecute gate (Continue read-only, Cancel, Create-only, no reentry decision) ──

    [Fact]  // (2) Cancel from the review does not execute
    public void WillExecute_ReviewNotConfirmed_False()
        => Assert.False(GroupDatCreatePresenter.WillExecute(GroupDatReconciliationMode.NewGroup, reviewConfirmed: false));

    [Fact]  // (3) Create Group (confirmed Create plan) executes
    public void WillExecute_ConfirmedCreate_True()
        => Assert.True(GroupDatCreatePresenter.WillExecute(GroupDatReconciliationMode.NewGroup, reviewConfirmed: true));

    [Fact]  // (4) Update mode never triggers ExecuteCreateAsync, even when confirmed
    public void WillExecute_UpdateMode_False()
        => Assert.False(GroupDatCreatePresenter.WillExecute(GroupDatReconciliationMode.UpdateGroup, reviewConfirmed: true));

    // ── Result → presentation mapping ─────────────────────────────────────────

    [Fact]  // (5) Committed → success + refresh
    public void Present_Committed_SuccessAndRefresh()
    {
        var p = GroupDatCreatePresenter.Present(Result(GroupDatExecutionStatus.Committed, published: 410), "Commodore 64 TOSEC");

        Assert.Equal(GroupDatCreatePresentationKind.Success, p.Kind);
        Assert.True(p.ShouldRefresh);
        Assert.Contains("created successfully", p.Message);
        Assert.Contains("Commodore 64 TOSEC", p.Message);
        Assert.Contains("c64-tosec", p.Message);
        Assert.Contains("410", p.Message);
        Assert.Empty(p.CleanupPaths);
    }

    [Fact]  // (6) AbortedNoWrites → error, no refresh, sanitized service message surfaced
    public void Present_Aborted_ErrorNoRefreshWithMessage()
    {
        var p = GroupDatCreatePresenter.Present(
            Result(GroupDatExecutionStatus.AbortedNoWrites, GroupDatExecutionErrorCode.GroupIdCollision, "Group id 'c64-tosec' already exists."),
            "Commodore 64 TOSEC");

        Assert.Equal(GroupDatCreatePresentationKind.Error, p.Kind);
        Assert.False(p.ShouldRefresh);
        Assert.Contains("was not created", p.Message);
        Assert.Contains("No catalog changes were committed", p.Message);
        Assert.Contains("already exists", p.Message);   // sanitized service ErrorMessage
    }

    [Fact]  // (7) Cancelled → cancellation mapping, no refresh
    public void Present_Cancelled_WarningNoRefresh()
    {
        var p = GroupDatCreatePresenter.Present(Result(GroupDatExecutionStatus.Cancelled), "Commodore 64 TOSEC");

        Assert.Equal(GroupDatCreatePresentationKind.Warning, p.Kind);
        Assert.False(p.ShouldRefresh);
        Assert.Contains("cancelled", p.Message);
        Assert.Contains("No Group was committed", p.Message);
    }

    [Fact]  // (8) CleanupRequired → warning with the exact paths, no refresh
    public void Present_CleanupRequired_WarningWithPaths()
    {
        var paths = new[] { @"data\systems\c64\c64-tosec-a.db", @"data\systems\c64\c64-tosec-b.db" };
        var p = GroupDatCreatePresenter.Present(Result(GroupDatExecutionStatus.CleanupRequired, cleanup: paths), "Commodore 64 TOSEC");

        Assert.Equal(GroupDatCreatePresentationKind.Warning, p.Kind);
        Assert.False(p.ShouldRefresh);
        Assert.Contains("Manual cleanup is required", p.Message);
        Assert.Equal(paths, p.CleanupPaths);
    }

    // ── Refresh rule (only on Committed) ──────────────────────────────────────

    [Theory]  // (9) refresh happens only for Committed
    [InlineData(GroupDatExecutionStatus.Committed,       true)]
    [InlineData(GroupDatExecutionStatus.AbortedNoWrites, false)]
    [InlineData(GroupDatExecutionStatus.Cancelled,       false)]
    [InlineData(GroupDatExecutionStatus.CleanupRequired, false)]
    public void ShouldRefresh_OnlyOnCommitted(GroupDatExecutionStatus status, bool expected)
        => Assert.Equal(expected, GroupDatCreatePresenter.ShouldRefresh(Result(status)));
}
