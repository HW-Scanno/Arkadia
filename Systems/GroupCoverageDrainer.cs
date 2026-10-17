using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Arkadia.Activities;

namespace Arkadia.Systems;

/// <summary>
/// The serialized worker (MAX CONCURRENCY = 1) that resolves the pending Group coverage of the Systems view, extracted
/// from MainWindow so its lifecycle — in particular the global LOADING activity — is unit-testable. Behaviour is the
/// one the code-behind always had: pull ONE pending Group from the <see cref="GroupCoverageQueue"/>, compute its
/// aggregate off the UI thread, record the result on the UI thread (the queue's generation token rejects an obsolete
/// result), refresh the view, then pull the next. All UI-thread work is delegated through <c>postToUi</c>.
///
/// <para><b>Activity lifecycle (the real Systems "loading").</b> Each compute holds one <see cref="ActivityKind.Loading"/>
/// scope from the moment it starts until its result has been applied on the UI thread (success, failure or obsolete).
/// The next compute — started from inside that same UI callback — opens ITS scope before this one is released, so the
/// console never flashes READY between two groups, and falls back to READY only after the last group has really
/// finished. Scopes are per compute, so a finishing older compute can never end a newer one. No timeout is involved.</para>
/// </summary>
public sealed class GroupCoverageDrainer
{
    private readonly GroupCoverageQueue _queue;
    private readonly Func<string, bool> _isKnown;
    private readonly Func<string, IReadOnlyList<(string DatLineId, string DataStorePath, int ReleaseCount)>> _leavesOf;
    private readonly Func<IReadOnlyList<(string DatLineId, string DataStorePath, int ReleaseCount)>, Task<GroupStorageFacts>> _compute;
    private readonly Action<Action> _postToUi;
    private readonly Action _refreshView;
    private readonly ActivityMonitor _activities;

    public GroupCoverageDrainer(
        GroupCoverageQueue queue,
        Func<string, bool> isKnown,
        Func<string, IReadOnlyList<(string DatLineId, string DataStorePath, int ReleaseCount)>> leavesOf,
        Func<IReadOnlyList<(string DatLineId, string DataStorePath, int ReleaseCount)>, Task<GroupStorageFacts>> compute,
        Action<Action> postToUi,
        Action refreshView,
        ActivityMonitor activities)
    {
        _queue = queue; _isKnown = isKnown; _leavesOf = leavesOf; _compute = compute;
        _postToUi = postToUi; _refreshView = refreshView; _activities = activities;
    }

    /// <summary>True while a compute is in flight (gates the single worker on the UI thread).</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Starts the next pending group if idle. Call on the UI thread.</summary>
    public void Drain()
    {
        if (IsBusy) return;
        if (_queue.Begin(_isKnown) is not { } started) return;

        IsBusy = true;
        var (groupId, generation) = started;
        var leaves = _leavesOf(groupId);

        var loading = _activities.Begin(ActivityKind.Loading, $"Systems: group coverage {groupId}");

        Task<GroupStorageFacts> work;
        try { work = _compute(leaves); }
        catch { loading.Dispose(); throw; }

        _ = work.ContinueWith(t => _postToUi(() =>
        {
            try
            {
                IsBusy = false;

                // The queue applies the generation guard: an obsolete result (invalidated mid-flight) returns false and is
                // discarded → re-enqueue for a fresh compute.
                if (t.IsCompletedSuccessfully)
                {
                    if (!_queue.Complete(groupId, generation, t.Result))
                        _queue.Enqueue(groupId, priority: false, _isKnown);
                }
                else
                {
                    _queue.Fail(groupId, generation);   // warning marker + retry-on-select
                }

                // Autonomous refresh (never gated on the current selection); preserves selection, no jumps.
                _refreshView();
                Drain();   // continue with the next pending group (opens ITS Loading scope before this one is released)
            }
            finally
            {
                loading.Dispose();   // success, failure or obsolete result: this compute's activity always ends here
            }
        }), TaskContinuationOptions.None);
    }
}
