using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Arkadia.Controls;

/// <summary>Source of periodic ticks. Abstracted so the clock is testable without a UI thread.</summary>
public interface IActivityTickSource
{
    void Start(TimeSpan interval, Action tick);
    void Stop();
}

/// <summary><see cref="IActivityTickSource"/> backed by one Avalonia <see cref="DispatcherTimer"/> (UI thread).</summary>
public sealed class DispatcherTickSource : IActivityTickSource
{
    private DispatcherTimer? _timer;

    public void Start(TimeSpan interval, Action tick)
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = interval };
        _timer.Tick += (_, _) => tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}

/// <summary>
/// The ONE animation clock for every "activity" visual (the sidebar C64 console today; every future
/// <c>LOADING...</c> indicator in the System View). Visuals never own timers: they <see cref="Subscribe"/> while
/// they need to animate and dispose the subscription when they stop (state change, detached from the tree). The
/// underlying timer exists only while at least one subscriber is registered, so an idle UI has no timer and no
/// periodic invalidation at all. Ticks are low-rate (<see cref="Interval"/>) by design — restrained motion.
///
/// <para>Reduced motion: when <see cref="ReducedMotion"/> is true the clock never starts a timer; visuals then
/// render a static frame.</para>
/// </summary>
public sealed class ActivityClock
{
    /// <summary>Tick period (12.5 Hz): enough for a gentle scroll, far below any flash-rate concern.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(80);

    private readonly IActivityTickSource _source;
    private readonly List<Action<long>> _subscribers = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();   // ONE timebase for the whole app: never restarted

    /// <summary>The application-wide clock (UI-thread dispatcher timer; reduced-motion auto-detected).</summary>
    public static ActivityClock Shared { get; } = new(new DispatcherTickSource(), ReducedMotionDetector.Detect());

    public ActivityClock(IActivityTickSource source, bool reducedMotion)
    {
        _source       = source ?? throw new ArgumentNullException(nameof(source));
        ReducedMotion = reducedMotion;
    }

    public bool ReducedMotion { get; }
    public int  SubscriberCount => _subscribers.Count;
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Milliseconds on the shared timebase (monotonic, never reset). Because the phase belongs to the clock and not
    /// to any subscriber, a control that appears halfway through a cycle is immediately in phase with the others.
    /// </summary>
    public long ElapsedMs => _watch.ElapsedMilliseconds;

    /// <summary>The animation frame (<see cref="Interval"/>-sized steps) of a timestamp — what every visual quantizes to.</summary>
    public static long FrameOf(long elapsedMs) => elapsedMs / (long)Interval.TotalMilliseconds;

    /// <summary>The current shared frame.</summary>
    public long Frame => FrameOf(ElapsedMs);

    /// <summary>Registers an animation callback (receives <see cref="ElapsedMs"/> on each tick). Dispose to unsubscribe.</summary>
    public IDisposable Subscribe(Action<long> onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        _subscribers.Add(onTick);
        if (_subscribers.Count == 1 && !ReducedMotion) StartTimer();
        return new Subscription(this, onTick);
    }

    private void StartTimer()
    {
        IsRunning = true;
        _source.Start(Interval, OnTick);
    }

    private void OnTick()
    {
        var now = _watch.ElapsedMilliseconds;
        foreach (var s in _subscribers.ToArray()) s(now);   // copy: a callback may unsubscribe
    }

    private void Unsubscribe(Action<long> onTick)
    {
        if (!_subscribers.Remove(onTick)) return;
        if (_subscribers.Count == 0 && IsRunning)
        {
            _source.Stop();
            IsRunning = false;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private ActivityClock? _clock;
        private readonly Action<long> _cb;
        public Subscription(ActivityClock clock, Action<long> cb) { _clock = clock; _cb = cb; }
        public void Dispose() { _clock?.Unsubscribe(_cb); _clock = null; }
    }
}

/// <summary>
/// Reduced-motion preference. Avalonia exposes no portable setting, so: the explicit
/// <c>ARKADIA_REDUCED_MOTION</c> environment variable wins on every platform; on Windows the system "show animations"
/// switch (<c>SPI_GETCLIENTAREAANIMATION</c>) is honoured. On Linux only the environment variable applies.
/// </summary>
public static class ReducedMotionDetector
{
    public static bool Detect() => Detect(Environment.GetEnvironmentVariable, QueryWindowsAnimationsEnabled);

    /// <param name="env">Environment lookup.</param>
    /// <param name="osAnimationsEnabled">OS query: true/false = animations on/off, null = unknown.</param>
    public static bool Detect(Func<string, string?> env, Func<bool?> osAnimationsEnabled)
    {
        var v = env("ARKADIA_REDUCED_MOTION")?.Trim();
        if (v is not null && v.Length > 0)
            return v is "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        return osAnimationsEnabled() == false;
    }

    private static bool? QueryWindowsAnimationsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out int on, 0) ? on != 0 : null;
        }
        catch { return null; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}
