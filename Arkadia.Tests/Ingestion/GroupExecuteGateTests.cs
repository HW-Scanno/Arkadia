using System.Collections.Immutable;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>Pure gate helpers for the Group execution consent barrier + membership drift guard (M9C2B).</summary>
public sealed class GroupExecuteGateTests
{
    [Fact] // 2: Ready allows Execute
    public void CanExecute_Ready_True()
        => Assert.True(GroupExecuteGate.CanExecute(GroupIngestionReadiness.Ready));

    [Fact] // 3: Blocked cannot Execute
    public void CanExecute_Blocked_False()
        => Assert.False(GroupExecuteGate.CanExecute(GroupIngestionReadiness.Blocked));

    [Fact] // 4: NothingToDo cannot Execute
    public void CanExecute_NothingToDo_False()
        => Assert.False(GroupExecuteGate.CanExecute(GroupIngestionReadiness.NothingToDo));

    [Fact]
    public void MembershipMatches_SameSet_True()
        => Assert.True(GroupExecuteGate.MembershipMatches(
            new[] { "b", "a", "c" }, ImmutableArray.Create("a", "b", "c")));

    [Fact] // 5: membership drift (leaf added) → mismatch → caller aborts
    public void MembershipMatches_LeafAdded_False()
        => Assert.False(GroupExecuteGate.MembershipMatches(
            new[] { "a", "b", "c" }, ImmutableArray.Create("a", "b")));

    [Fact] // membership drift (leaf removed) → mismatch
    public void MembershipMatches_LeafRemoved_False()
        => Assert.False(GroupExecuteGate.MembershipMatches(
            new[] { "a" }, ImmutableArray.Create("a", "b")));

    // ── Decide: the post-replan pre-mutation decision ────────────────────────

    [Fact] // equal membership + Ready → Proceed
    public void Decide_EqualReady_Proceed()
        => Assert.Equal(GroupExecuteDecision.Proceed, GroupExecuteGate.Decide(
            new[] { "a", "b" }, new[] { "a", "b" }, GroupIngestionReadiness.Ready));

    [Fact] // CORRECTION #1: initial guard passed, but the FRESH plan membership differs (leaf C added)
           // while Ready → abort BEFORE any mutation. Membership drift wins over readiness.
    public void Decide_FreshMembershipDiffersButReady_AbortMembershipChanged()
        => Assert.Equal(GroupExecuteDecision.AbortMembershipChanged, GroupExecuteGate.Decide(
            previewExpected: new[] { "a", "b" },
            freshExpected:   new[] { "a", "b", "c" },
            freshReadiness:  GroupIngestionReadiness.Ready));

    [Fact] // membership drift takes precedence even over a Blocked fresh plan
    public void Decide_MembershipDiffers_TakesPrecedenceOverBlocked()
        => Assert.Equal(GroupExecuteDecision.AbortMembershipChanged, GroupExecuteGate.Decide(
            new[] { "a", "b" }, new[] { "a" }, GroupIngestionReadiness.Blocked));

    [Fact] // equal membership + Blocked → AbortBlocked
    public void Decide_EqualBlocked_AbortBlocked()
        => Assert.Equal(GroupExecuteDecision.AbortBlocked, GroupExecuteGate.Decide(
            new[] { "a" }, new[] { "a" }, GroupIngestionReadiness.Blocked));

    [Fact] // equal membership + NothingToDo → NothingToDo
    public void Decide_EqualNothingToDo_NothingToDo()
        => Assert.Equal(GroupExecuteDecision.NothingToDo, GroupExecuteGate.Decide(
            new[] { "a" }, new[] { "a" }, GroupIngestionReadiness.NothingToDo));
}
