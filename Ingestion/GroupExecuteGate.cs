using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Ingestion;

/// <summary>The pre-mutation decision for a Group execution after the fresh read-only re-plan.</summary>
public enum GroupExecuteDecision
{
    /// <summary>Membership unchanged and Ready — proceed to the first mutation (pre-extraction).</summary>
    Proceed,
    /// <summary>Membership drifted from the previewed set — abort, require re-opening the preview.</summary>
    AbortMembershipChanged,
    /// <summary>Fresh plan is Blocked — abort with diagnostics.</summary>
    AbortBlocked,
    /// <summary>Nothing to ingest — complete cleanly, no mutation.</summary>
    NothingToDo,
}

/// <summary>
/// Small pure gate helpers for the Group execution bridge: which readiness states may Execute, and whether the
/// live membership still matches the plan the user reviewed. No I/O, no mutation.
/// </summary>
public static class GroupExecuteGate
{
    /// <summary>Only a <see cref="GroupIngestionReadiness.Ready"/> plan may execute. Blocked / NothingToDo may not.</summary>
    public static bool CanExecute(GroupIngestionReadiness readiness) => readiness == GroupIngestionReadiness.Ready;

    /// <summary>
    /// The pre-mutation decision after the fresh re-plan. Membership equality (fresh vs the previewed set) is
    /// checked FIRST — a drift is a consent violation that must abort regardless of readiness, closing the
    /// narrow window where membership changes between the initial live guard and the fresh plan. Only
    /// <see cref="GroupExecuteDecision.Proceed"/> permits any mutation.
    /// </summary>
    public static GroupExecuteDecision Decide(
        IEnumerable<string>     previewExpected,
        IEnumerable<string>     freshExpected,
        GroupIngestionReadiness freshReadiness)
    {
        if (!MembershipMatches(freshExpected, previewExpected)) return GroupExecuteDecision.AbortMembershipChanged;
        if (freshReadiness == GroupIngestionReadiness.Blocked)  return GroupExecuteDecision.AbortBlocked;
        if (freshReadiness == GroupIngestionReadiness.NothingToDo) return GroupExecuteDecision.NothingToDo;
        return GroupExecuteDecision.Proceed;
    }

    /// <summary>
    /// Ordinal set-equality of the live leaf ids vs the plan the user reviewed. A mismatch means membership
    /// drifted since the preview → the caller must abort before any mutation.
    /// </summary>
    public static bool MembershipMatches(IEnumerable<string> live, IEnumerable<string> expected)
    {
        var liveSet = new HashSet<string>(live, StringComparer.Ordinal);
        var expSet  = new HashSet<string>(expected, StringComparer.Ordinal);
        return liveSet.SetEquals(expSet);
    }
}
