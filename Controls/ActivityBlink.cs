using System;
using Avalonia;
using Avalonia.Controls;

namespace Arkadia.Controls;

/// <summary>
/// Synchronized blinking for any control, driven by the shared <see cref="ActivityClock"/>. It changes
/// <see cref="Visual.Opacity"/> and nothing else — never size, margin or layout — and uses the same phase as the
/// console's LOADING text, so every LOADING indicator blinks together. It subscribes to the clock only while the
/// control is attached to the UI, and not at all under reduced motion (the control then stays fully opaque).
/// </summary>
public sealed class ActivityBlink
{
    /// <summary>Opacity during the "on" part of the cycle.</summary>
    public const double OnOpacity = 1.0;

    /// <summary>Opacity during the "off" part: faint, not invisible, so the row never looks empty.</summary>
    public const double OffOpacity = 0.2;

    /// <summary>Blink opacity at a shared-timebase timestamp: on 600 ms of every 1000 ms (the console's LOADING phase).</summary>
    public static double OpacityAt(long elapsedMs, bool reducedMotion)
        => reducedMotion || (elapsedMs % 1000) < 600 ? OnOpacity : OffOpacity;

    private readonly Control _control;
    private readonly ActivityClock _clock;
    private IDisposable? _subscription;

    private ActivityBlink(Control control, ActivityClock clock) { _control = control; _clock = clock; }

    /// <summary>Makes <paramref name="control"/> blink while it is in the visual tree.</summary>
    public static ActivityBlink Attach(Control control, ActivityClock? clock = null)
    {
        var blink = new ActivityBlink(control, clock ?? ActivityClock.Shared);
        control.AttachedToVisualTree   += (_, _) => blink.SetAttached(true);
        control.DetachedFromVisualTree += (_, _) => blink.SetAttached(false);
        return blink;
    }

    /// <summary>Attachment bookkeeping (also the test seam: a visual root needs a windowing platform).</summary>
    internal void SetAttached(bool attached)
    {
        if (attached && !_clock.ReducedMotion)
        {
            if (_subscription is null)
            {
                Apply(_clock.ElapsedMs);                          // join the shared phase immediately
                _subscription = _clock.Subscribe(Apply);
            }
        }
        else
        {
            _subscription?.Dispose();
            _subscription = null;
            _control.Opacity = OnOpacity;                         // detached / reduced motion: steady
        }
    }

    public bool IsSubscribed => _subscription is not null;

    private void Apply(long elapsedMs)
    {
        var o = OpacityAt(elapsedMs, reducedMotion: false);
        if (_control.Opacity != o) _control.Opacity = o;          // only a repaint; no layout
    }
}
