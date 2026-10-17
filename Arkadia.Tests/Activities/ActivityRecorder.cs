using System.Collections.Generic;
using Arkadia.Activities;

namespace Arkadia.Tests.Activities;

/// <summary>
/// A real <see cref="ActivityMonitor"/> with SYNCHRONOUS delivery (no dispatcher) that records every foreground
/// change, so a test sees exactly what the C64 console would be told, in order: [Verifying, null] = one run and a
/// return to READY; a null in the middle of a run = a false READY.
/// </summary>
public sealed class ActivityRecorder
{
    public ActivityMonitor Monitor { get; } = new();
    public List<ActivityKind?> Seen { get; } = new();

    public ActivityRecorder() => Monitor.ForegroundChanged += k => Seen.Add(k);

    /// <summary>The foreground right now (null = READY).</summary>
    public ActivityKind? Now => Monitor.Foreground;

    public bool Idle => Monitor.IsIdle;
}
