using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Arkadia.Activities;

/// <summary>A running activity, as returned by <see cref="ActivityMonitor.Begin"/>. Dispose to end it (idempotent).</summary>
public interface IActivityScope : IDisposable
{
    long Id { get; }
    ActivityKind Kind { get; }
    string? Detail { get; }
    /// <summary>True until the scope is disposed.</summary>
    bool IsActive { get; }
}

/// <summary>Read-only view of one active activity.</summary>
public sealed record ActivityInfo(long Id, ActivityKind Kind, string? Detail);

/// <summary>
/// Central registry of what the application is currently doing. Workflows only do
/// <c>using var activity = monitor.Begin(ActivityKind.Verifying);</c> — they never set or reset a global status,
/// and there is no boolean "busy" flag anywhere: the status is DERIVED from the set of live scopes, so overlapping
/// operations cannot cancel each other and the idle state is reported only when no scope is alive.
///
/// <para><b>Foreground policy.</b> The reported foreground is the active scope with the highest
/// <see cref="ActivityCatalog.Priority"/>; among scopes of the same kind the OLDEST wins (lowest id), so the
/// display never flips between equals. When the foreground scope ends, the next-best still-active one is reported
/// automatically; with none left, the foreground is <c>null</c> (READY).</para>
///
/// <para><b>Threads.</b> <see cref="Begin"/> and <c>Dispose</c> may be called from any thread (state is guarded by
/// a lock). <see cref="ForegroundChanged"/> is raised through the supplied <c>post</c> delegate — the UI dispatcher
/// in the app — and re-reads the CURRENT foreground when it runs, so a burst of changes coalesces into the true
/// final state and a late notification can never report a stale idle.</para>
///
/// <para><b>Leaks.</b> A scope that is garbage-collected without being disposed ends itself (finalizer safety net),
/// so a forgotten handle cannot keep the console busy forever.</para>
/// </summary>
public sealed class ActivityMonitor
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _active = new();
    private readonly Action<Action> _post;
    private long _nextId;
    private readonly object _raiseGate = new();
    private ActivityKind? _raised;   // last value delivered to listeners (guarded by _raiseGate)

    /// <summary>The application-wide monitor; change notifications are marshalled to the UI thread.</summary>
    public static ActivityMonitor Shared { get; } = new(a => Dispatcher.UIThread.Post(a));

    /// <param name="post">Marshals change notifications (UI thread). Null = deliver synchronously on the calling thread.</param>
    public ActivityMonitor(Action<Action>? post = null) => _post = post ?? (a => a());

    /// <summary>Raised (via the post delegate) when the foreground activity changes; <c>null</c> means READY.</summary>
    public event Action<ActivityKind?>? ForegroundChanged;

    /// <summary>Starts an activity. Always pair with Dispose (use a <c>using</c>/<c>finally</c>).</summary>
    public IActivityScope Begin(ActivityKind kind, string? detail = null)
    {
        Entry entry;
        lock (_gate)
        {
            entry = new Entry(++_nextId, kind, detail);
            _active[entry.Id] = entry;
        }
        var scope = new Scope(this, entry);
        Notify();
        return scope;
    }

    /// <summary>The foreground activity right now, or null when nothing is registered.</summary>
    public ActivityKind? Foreground
    {
        get { lock (_gate) return ForegroundLocked()?.Kind; }
    }

    public int ActiveCount { get { lock (_gate) return _active.Count; } }

    public bool IsIdle => ActiveCount == 0;

    /// <summary>Snapshot of all active activities, foreground first (priority desc, then oldest).</summary>
    public IReadOnlyList<ActivityInfo> Active
    {
        get
        {
            lock (_gate)
                return _active.Values.OrderBy(e => -ActivityCatalog.Priority(e.Kind)).ThenBy(e => e.Id)
                    .Select(e => new ActivityInfo(e.Id, e.Kind, e.Detail)).ToList();
        }
    }

    /// <summary>Runs <paramref name="work"/> inside an activity; the scope always ends (success, failure, cancellation).</summary>
    public async Task RunAsync(ActivityKind kind, Func<Task> work, string? detail = null)
    {
        using var scope = Begin(kind, detail);
        await work().ConfigureAwait(false);
    }

    public async Task<T> RunAsync<T>(ActivityKind kind, Func<Task<T>> work, string? detail = null)
    {
        using var scope = Begin(kind, detail);
        return await work().ConfigureAwait(false);
    }

    public void Run(ActivityKind kind, Action work, string? detail = null)
    {
        using var scope = Begin(kind, detail);
        work();
    }

    public T Run<T>(ActivityKind kind, Func<T> work, string? detail = null)
    {
        using var scope = Begin(kind, detail);
        return work();
    }

    private Entry? ForegroundLocked()
    {
        Entry? best = null;
        foreach (var e in _active.Values)
        {
            if (best is null) { best = e; continue; }
            var pe = ActivityCatalog.Priority(e.Kind);
            var pb = ActivityCatalog.Priority(best.Kind);
            if (pe > pb || (pe == pb && e.Id < best.Id)) best = e;
        }
        return best;
    }

    private bool End(long id)
    {
        bool removed;
        lock (_gate) removed = _active.Remove(id);
        if (removed) Notify();
        return removed;
    }

    private void Notify() => _post(() =>
    {
        var current = Foreground;                  // read at delivery time: always the truth, never a stale value
        lock (_raiseGate)
        {
            if (current == _raised) return;
            _raised = current;
        }
        ForegroundChanged?.Invoke(current);
    });

    private sealed record Entry(long Id, ActivityKind Kind, string? Detail);

    private sealed class Scope : IActivityScope
    {
        private readonly ActivityMonitor _monitor;
        private readonly Entry _entry;
        private int _ended;

        public Scope(ActivityMonitor monitor, Entry entry) { _monitor = monitor; _entry = entry; }

        public long Id => _entry.Id;
        public ActivityKind Kind => _entry.Kind;
        public string? Detail => _entry.Detail;
        public bool IsActive => Volatile.Read(ref _ended) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            GC.SuppressFinalize(this);
            _monitor.End(_entry.Id);
        }

        // Safety net: a handle that was never disposed must not leave the console busy for the rest of the session.
        ~Scope()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) _monitor.End(_entry.Id);
        }
    }
}
