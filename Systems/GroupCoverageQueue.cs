using System;
using System.Collections.Generic;

namespace Arkadia.Systems;

/// <summary>
/// The pure state machine behind autonomous, SERIALIZED (max concurrency 1) Group-aggregate convergence. It owns
/// the session cache, the in-flight guard, the failed set, a per-group generation token, and the pending order —
/// but performs NO I/O, no threading, and no Avalonia work. MainWindow drives the actual off-thread compute
/// around it (Begin → Task.Run(Compute) → Complete/Fail), so all the ordering / dedup / priority / obsolete-result
/// logic is unit-testable in isolation.
///
/// Invariants: a cached, in-flight, or failed group is never (auto-)queued; only an explicit <see cref="Retry"/>
/// re-admits a failed group; <see cref="Begin"/> yields nothing while one compute is already in flight; and a
/// result whose generation no longer matches (the group was invalidated mid-flight) is rejected so a stale read
/// can never overwrite newer state.
/// </summary>
public sealed class GroupCoverageQueue
{
    private readonly Dictionary<string, GroupStorageFacts> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed   = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _gen = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _queue = new();

    public bool TryGet(string groupId, out GroupStorageFacts facts) => _cache.TryGetValue(groupId, out facts);
    public bool IsCached(string groupId)   => _cache.ContainsKey(groupId);
    public bool IsFailed(string groupId)   => _failed.Contains(groupId);
    public bool IsInFlight(string groupId) => _inFlight.Contains(groupId);
    public bool IsQueued(string groupId)   => _queue.Contains(groupId);

    public int QueuedCount   => _queue.Count;
    public int InFlightCount => _inFlight.Count;

    /// <summary>The current generation token for a group (bumped by <see cref="Invalidate"/>).</summary>
    public int Generation(string groupId) => _gen.GetValueOrDefault(groupId);

    /// <summary>
    /// Adds a group to the pending order unless it is cached / in-flight / failed / unknown. When
    /// <paramref name="priority"/> it is placed (or moved) at the FRONT — the selection-priority path. Dedup keeps
    /// a group at most once in the queue.
    /// </summary>
    public void Enqueue(string groupId, bool priority, Func<string, bool> isKnown)
    {
        if (_cache.ContainsKey(groupId) || _inFlight.Contains(groupId) || _failed.Contains(groupId)) return;
        if (!isKnown(groupId)) return;

        var node = _queue.Find(groupId);
        if (node is not null)
        {
            if (priority && node != _queue.First)
            {
                _queue.Remove(node);
                _queue.AddFirst(groupId);
            }
            return;
        }
        if (priority) _queue.AddFirst(groupId);
        else          _queue.AddLast(groupId);
    }

    /// <summary>
    /// Pops the next actionable group and marks it in-flight, or returns null if one is already in flight (the
    /// concurrency bound) or nothing actionable remains. Skips entries that became cached / in-flight / unknown
    /// while queued. The returned generation must be handed back to <see cref="Complete"/>/<see cref="Fail"/>.
    /// </summary>
    public (string GroupId, int Generation)? Begin(Func<string, bool> isKnown)
    {
        if (_inFlight.Count > 0) return null;   // MAX CONCURRENCY = 1

        while (_queue.Count > 0)
        {
            var g = _queue.First!.Value;
            _queue.RemoveFirst();
            if (_cache.ContainsKey(g) || _inFlight.Contains(g) || !isKnown(g)) continue;
            _inFlight.Add(g);
            return (g, _gen.GetValueOrDefault(g));
        }
        return null;
    }

    /// <summary>
    /// Records a successful result. Returns false (and stores nothing) when the group was invalidated since
    /// <see cref="Begin"/> (generation drift) — the caller should re-enqueue for a fresh compute.
    /// </summary>
    public bool Complete(string groupId, int startGeneration, GroupStorageFacts facts)
    {
        _inFlight.Remove(groupId);
        if (_gen.GetValueOrDefault(groupId) != startGeneration) return false;   // obsolete
        _cache[groupId] = facts;
        return true;
    }

    /// <summary>Records a fault. Returns false (marks nothing) if the group was invalidated mid-flight.</summary>
    public bool Fail(string groupId, int startGeneration)
    {
        _inFlight.Remove(groupId);
        if (_gen.GetValueOrDefault(groupId) != startGeneration) return false;   // obsolete
        _failed.Add(groupId);
        return true;
    }

    /// <summary>
    /// Targeted invalidation at a mutation boundary: drops cached/failed state and bumps the generation so any
    /// in-flight compute's result is rejected. The group becomes eligible to enqueue again.
    /// </summary>
    public void Invalidate(string groupId)
    {
        _cache.Remove(groupId);
        _failed.Remove(groupId);
        _gen[groupId] = _gen.GetValueOrDefault(groupId) + 1;
    }

    /// <summary>Clears a group's failed state so it can be re-enqueued — one explicit retry (e.g. on selection).</summary>
    public void Retry(string groupId) => _failed.Remove(groupId);
}
