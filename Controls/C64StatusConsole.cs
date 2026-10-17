using System;
using System.Globalization;
using System.Linq;
using Arkadia.Activities;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Arkadia.Controls;

/// <summary>Pure visual rules of the C64 console (colours, text, blink, raster motion, text fitting) — unit-tested.</summary>
public static class C64ConsoleVisuals
{
    // C64 (Colodore-like) palette. LightBlue is nudged lighter than the original for legibility on the blue screen.
    public static readonly Color Blue      = Color.Parse("#40318D");
    public static readonly Color LightBlue = Color.Parse("#A59CF2");

    /// <summary>Cassette-loading border colours, in cycle order (red, cyan, purple, green, yellow, light blue).</summary>
    public static readonly Color[] Stripes =
    {
        Color.Parse("#883932"), Color.Parse("#67B6BD"), Color.Parse("#8B4F96"),
        Color.Parse("#55A049"), Color.Parse("#BFCE72"), Color.Parse("#7869C4"),
    };

    /// <summary>PURGING only: black console, red text, lava region. Every other state keeps the blue C64 look.</summary>
    public static readonly Color PurgeBlack = Color.Parse("#000000");
    public static readonly Color PurgeRed   = Color.Parse("#FF2A2A");
    public static readonly Color PurgeEdge  = Color.Parse("#5A0000");

    /// <summary>Lava heat levels, coolest first: dark red, red, bright red, orange, hot yellow-orange.</summary>
    public static readonly Color[] Lava =
    {
        Color.Parse("#5A0000"), Color.Parse("#A80000"), Color.Parse("#E01010"), Color.Parse("#FF6A00"), Color.Parse("#FFB000"),
    };

    public const string ReadyText = ActivityCatalog.ReadyLabel;
    public static readonly string LoadingText = ActivityCatalog.Label(ActivityKind.Loading);

    public const int    FrameThickness = 6;     // border: raster bands / pattern strips live here
    public const int    StripeHeight   = 6;     // px per raster band
    public const double DefaultWidth   = 180;
    public const double DefaultHeight  = 40;

    /// <summary>The font is drawn on its native 8x8 grid: 16 px (2x) when it fits, else 8 px (1x).</summary>
    public const double LargeFontSize = 16, SmallFontSize = 8;

    /// <summary>Console text: <c>READY.</c> for no activity, otherwise the kind's label.</summary>
    public static string TextFor(ActivityKind? kind) => kind is { } k ? ActivityCatalog.Label(k) : ReadyText;

    /// <summary>
    /// Blink phase. Only the raster ("LOADING...") and the lava ("PURGING...") texts blink — visible 600 ms of every
    /// 1000 ms (~1 Hz); every other state, and READY, is steady. Reduced motion never blinks.
    /// </summary>
    public static bool TextVisible(ActivityKind? kind, long elapsedMs, bool reducedMotion)
        => kind is not { } k || ActivityCatalog.PatternFor(k) is not (ActivityPattern.Raster or ActivityPattern.Lava) || reducedMotion || (elapsedMs % 1000) < 600;

    /// <summary>Vertical scroll offset of the raster bands (1 px per frame). Zero when motion is reduced.</summary>
    public static int StripeOffset(long elapsedMs, bool reducedMotion)
        => reducedMotion ? 0 : (int)(Arkadia.Controls.ActivityClock.FrameOf(elapsedMs) % (StripeHeight * Stripes.Length));

    /// <summary>Colour of the band covering row <paramref name="y"/> for the given scroll offset.</summary>
    public static Color StripeColorAt(int y, int offset)
    {
        var n = StripeHeight * Stripes.Length;
        var row = (((y + offset) % n) + n) % n;
        return Stripes[row / StripeHeight];
    }

    /// <summary>Index into <see cref="Stripes"/> of the band covering row <paramref name="y"/>.</summary>
    public static int StripeIndexAt(int y, int offset)
    {
        var n = StripeHeight * Stripes.Length;
        return ((((y + offset) % n) + n) % n) / StripeHeight;
    }

    /// <summary>Largest of the two grid-aligned sizes whose text fits the screen width (monospace: 1 em per char).</summary>
    public static double FontSizeFor(string text, double screenWidth)
        => text.Length * LargeFontSize <= screenWidth ? LargeFontSize : SmallFontSize;
}

/// <summary>
/// Compact Commodore 64-style status console. <see cref="Kind"/> null = READY (static blue screen, light-blue border,
/// no animation, no clock subscription). A kind shows its label and the procedural animation of its
/// <see cref="ActivityPattern"/> (the kind → pattern mapping lives in <see cref="ActivityCatalog"/>, so workflows and
/// patterns stay decoupled): raster bars, block flow, fluctuating columns, hex stream or a scanner. No progress value
/// is ever shown.
///
/// <para>Animation is driven by the shared <see cref="ActivityClock"/> only: the control subscribes while it is
/// attached AND showing an activity AND motion is not reduced, and unsubscribes otherwise. It reads the clock's single
/// timebase, so it is in phase with every other indicator even when it appears mid-cycle. Animation only repaints
/// (no layout) and allocates nothing per frame: brushes are static and text is cached.</para>
///
/// <para>Font: embedded "C64 Pro Mono" (Style64) at <c>avares://Arkadia/Assets/Fonts</c>.</para>
/// </summary>
public sealed class C64StatusConsole : Control
{
    public const string FontUri = "avares://Arkadia/Assets/Fonts#C64 Pro Mono";
    private static readonly Typeface ConsoleTypeface = new(new FontFamily(FontUri));

    private static readonly IBrush BlueBrush      = new ImmutableSolidColorBrush(C64ConsoleVisuals.Blue);
    private static readonly IBrush LightBlueBrush = new ImmutableSolidColorBrush(C64ConsoleVisuals.LightBlue);
    private static readonly IBrush SweepBarBrush = new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BlackBrush     = new ImmutableSolidColorBrush(C64ConsoleVisuals.PurgeBlack);
    private static readonly IBrush PurgeRedBrush  = new ImmutableSolidColorBrush(C64ConsoleVisuals.PurgeRed);
    private static readonly IBrush PurgeEdgeBrush = new ImmutableSolidColorBrush(C64ConsoleVisuals.PurgeEdge);
    private static readonly IBrush[] LavaBrushes  = Array.ConvertAll(C64ConsoleVisuals.Lava, c => (IBrush)new ImmutableSolidColorBrush(c));
    private static readonly IBrush[] StripeBrushes = Array.ConvertAll(C64ConsoleVisuals.Stripes, c => (IBrush)new ImmutableSolidColorBrush(c));
    // scanner levels: idle, trail 2 behind, trail 1 behind, head (the head is the darkest, so it stands out)
    private static readonly IBrush[] ScanBrushes =
    {
        new ImmutableSolidColorBrush(Color.Parse("#8F84E8")), new ImmutableSolidColorBrush(Color.Parse("#7869C4")),
        new ImmutableSolidColorBrush(Color.Parse("#5A4BA8")), new ImmutableSolidColorBrush(C64ConsoleVisuals.Blue),
    };

    public static readonly StyledProperty<ActivityKind?> KindProperty =
        AvaloniaProperty.Register<C64StatusConsole, ActivityKind?>(nameof(Kind));

    private ActivityClock _clock = ActivityClock.Shared;
    private IDisposable?  _subscription;
    private long          _elapsedMs;
    private bool          _attached;
    private FormattedText? _text;
    private ActivityKind?  _textKind;
    private double         _textSize;

    static C64StatusConsole()
    {
        AffectsRender<C64StatusConsole>(KindProperty);
    }

    public C64StatusConsole()
    {
        // Pixel-crisp glyphs: the font is a bitmap-style design on an 8 px grid.
        RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Alias);
        UpdateAutomation();
    }

    /// <summary>The activity to show; null = READY.</summary>
    public ActivityKind? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>The clock driving the animation (the shared one by default; replaceable for tests).</summary>
    public ActivityClock Clock
    {
        get => _clock;
        set { _clock = value ?? throw new ArgumentNullException(nameof(value)); UpdateSubscription(); }
    }

    /// <summary>True while this control holds a clock subscription (i.e. it is animating).</summary>
    public bool HasClockSubscription => _subscription is not null;

    /// <summary>The shared-timebase timestamp this control last painted/synced to (test seam).</summary>
    internal long PhaseMs => _elapsedMs;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SetAttached(true);
    }

    /// <summary>Attachment bookkeeping (also the test seam: a visual root needs a windowing platform).</summary>
    internal void SetAttached(bool attached)
    {
        _attached = attached;
        UpdateSubscription();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        SetAttached(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty)
        {
            UpdateSubscription();
            UpdateAutomation();
        }
    }

    /// <summary>Subscribes to the shared clock iff animation is needed; otherwise releases it (no timer at rest).</summary>
    internal void UpdateSubscription()
    {
        var need = _attached && Kind is not null && !_clock.ReducedMotion;
        if (need && _subscription is null)
        {
            _elapsedMs = _clock.ElapsedMs;                       // join the shared phase immediately
            _subscription = _clock.Subscribe(ms => { _elapsedMs = ms; InvalidateVisual(); });
        }
        else if (!need && _subscription is not null)
        {
            _subscription.Dispose();
            _subscription = null;
        }
        else if (need)
            _elapsedMs = _clock.ElapsedMs;                       // kind changed while animating: re-sync, same clock
    }

    private void UpdateAutomation()
        => AutomationProperties.SetName(this, Kind is { } k ? $"Activity status: {k.ToString().ToLowerInvariant()}" : "Activity status: ready");

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? C64ConsoleVisuals.DefaultWidth : availableSize.Width;
        return new Size(w, C64ConsoleVisuals.DefaultHeight);
    }

    public override void Render(DrawingContext context)
    {
        int w = (int)Math.Floor(Bounds.Width);
        int h = (int)Math.Floor(Bounds.Height);
        if (w <= 0 || h <= 0) return;

        var kind    = Kind;
        var reduced = _clock.ReducedMotion;
        int band    = C64ConsoleVisuals.FrameThickness;
        // Reduced motion (or READY) renders frame 0: a static picture of the same pattern.
        long frame  = reduced || kind is null ? 0 : ActivityClock.FrameOf(_elapsedMs);
        ActivityPattern? pattern = kind is { } k ? ActivityCatalog.PatternFor(k) : null;

        bool lava = pattern == ActivityPattern.Lava;

        // Border.
        if (lava)
        {
            context.FillRectangle(PurgeEdgeBrush, new Rect(0, 0, w, h));                 // 1 px dark-red outline,
            context.FillRectangle(BlackBrush, new Rect(1, 1, Math.Max(0, w - 2), Math.Max(0, h - 2)));   // black console
        }
        else if (pattern == ActivityPattern.Raster)
            DrawRaster(context, w, h, C64ConsoleVisuals.StripeOffset(_elapsedMs, reduced));
        else
            context.FillRectangle(LightBlueBrush, new Rect(0, 0, w, h));

        if (pattern is { } p && p != ActivityPattern.Raster && p != ActivityPattern.Lava)
        {
            using (context.PushClip(new Rect(0, 0, w, band)))
                DrawStrip(context, p, w, 0, frame, strip: 0);
            using (context.PushClip(new Rect(0, h - band, w, band)))
                DrawStrip(context, p, w, h - band, frame, strip: 1);
        }

        // Screen.
        var screen = new Rect(band, band, Math.Max(0, w - 2 * band), Math.Max(0, h - 2 * band));
        context.FillRectangle(lava ? BlackBrush : BlueBrush, screen);

        // PURGING: the lava region takes the bottom of the screen; the text lives in the part above it.
        var textArea = screen;
        if (lava)
        {
            int lavaH = Math.Min(C64ConsolePatterns.LavaHeight, (int)screen.Height / 2);
            var region = new Rect(screen.X, screen.Bottom - lavaH, screen.Width, lavaH);
            DrawLava(context, region, frame);
            textArea = new Rect(screen.X, screen.Y, screen.Width, Math.Max(0, screen.Height - lavaH - 2));
        }

        if (!C64ConsoleVisuals.TextVisible(kind, _elapsedMs, reduced)) return;

        var text = C64ConsoleVisuals.TextFor(kind);
        var size = C64ConsoleVisuals.FontSizeFor(text, screen.Width);
        if (_text is null || _textKind != kind || _textSize != size)
        {
            _text = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                ConsoleTypeface, size, lava ? PurgeRedBrush : LightBlueBrush);
            _textKind = kind;
            _textSize = size;
        }
        var origin = new Point(
            textArea.X + Math.Floor((textArea.Width  - _text.Width)  / 2),
            textArea.Y + Math.Floor((textArea.Height - _text.Height) / 2));
        using (context.PushClip(textArea))
            context.DrawText(_text, origin);
    }

    /// <summary>The lava rectangle: 2 px cells coloured by <see cref="C64ConsolePatterns.LavaLevel"/>; same-level runs are merged per row.</summary>
    private static void DrawLava(DrawingContext ctx, Rect region, long frame)
    {
        int cols = C64ConsolePatterns.LavaCols((int)region.Width);
        int rows = Math.Min(C64ConsolePatterns.LavaRows, (int)Math.Ceiling(region.Height / C64ConsolePatterns.LavaCell));
        const int C = C64ConsolePatterns.LavaCell;
        using (ctx.PushClip(region))
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int lvl = C64ConsolePatterns.LavaLevel(c, r, frame), c0 = c;
                    while (c + 1 < cols && C64ConsolePatterns.LavaLevel(c + 1, r, frame) == lvl) c++;
                    ctx.FillRectangle(LavaBrushes[lvl], new Rect(region.X + c0 * C, region.Y + r * C, (c - c0 + 1) * C, C));
                }
    }

    private static void DrawRaster(DrawingContext context, int w, int h, int offset)
    {
        for (int y = 0; y < h; y++)
        {
            int y0 = y;
            int idx = C64ConsoleVisuals.StripeIndexAt(y, offset);
            while (y + 1 < h && C64ConsoleVisuals.StripeIndexAt(y + 1, offset) == idx) y++;
            context.FillRectangle(StripeBrushes[idx], new Rect(0, y0, w, y - y0 + 1));
        }
    }

    /// <summary>Draws one 6 px strip (top = 0, bottom = 1) of a non-raster pattern at vertical position <paramref name="y0"/>.</summary>
    private static void DrawStrip(DrawingContext ctx, ActivityPattern pattern, int w, int y0, long frame, int strip)
    {
        const int T = C64ConsoleVisuals.FrameThickness;
        switch (pattern)
        {
            case ActivityPattern.BlockFlow:
            {
                int off = C64ConsolePatterns.FlowOffset(frame, strip);
                for (int x = off - C64ConsolePatterns.FlowPeriod; x < w; x += C64ConsolePatterns.FlowPeriod)
                    ctx.FillRectangle(BlueBrush, new Rect(x, y0 + 1, C64ConsolePatterns.FlowBlock, C64ConsolePatterns.FlowBlock));
                break;
            }
            case ActivityPattern.Columns:
            {
                int n = C64ConsolePatterns.ColumnCount(w);
                for (int i = 0; i < n; i++)
                {
                    int hgt = C64ConsolePatterns.ColumnHeight(i, frame, strip);
                    int y = strip == 0 ? y0 : y0 + T - hgt;               // top strip hangs, bottom strip rises
                    ctx.FillRectangle(BlueBrush, new Rect(i * C64ConsolePatterns.ColumnPitch, y, C64ConsolePatterns.ColumnWidth, hgt));
                }
                break;
            }
            case ActivityPattern.HexStream:
            {
                int n = C64ConsolePatterns.HexCount(w);
                for (int i = 0; i < n; i++)
                {
                    int v = C64ConsolePatterns.HexValue(i, frame, strip);
                    for (int row = 0; row < C64ConsolePatterns.HexRowsPerGlyph; row++)
                        for (int col = 0; col < C64ConsolePatterns.HexCols; col++)
                            if (C64ConsolePatterns.HexPixel(v, col, row))
                                ctx.FillRectangle(BlueBrush, new Rect(i * C64ConsolePatterns.HexPitch + col, y0 + 1 + row, 1, 1));
                }
                break;
            }
            case ActivityPattern.Sweep:
            {
                int n = C64ConsolePatterns.SweepCols(w);
                const int S = C64ConsolePatterns.SweepCell;
                for (int i = 0; i < n; i++)
                    for (int row = 0; row < C64ConsolePatterns.SweepRows; row++)
                    {
                        int lvl = C64ConsolePatterns.SweepLevel(i, row, frame, n, strip);
                        if (lvl == 0) continue;
                        ctx.FillRectangle(lvl == 2 ? SweepBarBrush : BlueBrush, new Rect(i * S, y0 + row * S, S, S));
                    }
                break;
            }
            case ActivityPattern.Scanner:
            {
                int n = C64ConsolePatterns.ScanCount(w);
                var (head, dir) = C64ConsolePatterns.ScanHead(frame, n);
                for (int i = 0; i < n; i++)
                    ctx.FillRectangle(ScanBrushes[C64ConsolePatterns.ScanLevel(i, head, dir)],
                        new Rect(i * C64ConsolePatterns.ScanPitch, y0 + 1, C64ConsolePatterns.ScanBlock, C64ConsolePatterns.ScanBlock));
                break;
            }
        }
    }
}

/// <summary>
/// Development-only preview of the console animations, driven through the REAL activity pipeline
/// (<see cref="ActivityMonitor"/> → console) without any real operation or database. Selected by the
/// <c>ARKADIA_C64_CONSOLE_PREVIEW</c> environment variable (unset in normal use → READY):
/// a kind name (<c>moving</c>, <c>verifying</c>, …) holds that activity; <c>cycle</c> shows every kind for 3 s each;
/// <c>overlap</c> demonstrates the priority policy (LOADING, then VERIFYING and MOVING joining and leaving).
/// </summary>
public static class C64ConsolePreview
{
    public const string EnvironmentVariable = "ARKADIA_C64_CONSOLE_PREVIEW";
    public const int DwellMs = 3000;

    /// <summary>One scripted activity: runs from <see cref="StartMs"/> (inclusive) to <see cref="EndMs"/> (exclusive; null = forever).</summary>
    public sealed record Step(ActivityKind Kind, long StartMs, long? EndMs);

    /// <summary>Parses the variable; returns null (no preview) for unset/unknown values.</summary>
    public static System.Collections.Generic.IReadOnlyList<Step>? Parse(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;

        if (v.Equals("cycle", StringComparison.OrdinalIgnoreCase))
            return Enum.GetValues<ActivityKind>().Select((k, i) => new Step(k, i * (long)DwellMs, (i + 1) * (long)DwellMs)).ToList();

        if (v.Equals("overlap", StringComparison.OrdinalIgnoreCase))
            return new[]
            {
                new Step(ActivityKind.Loading,   0,    9000),
                new Step(ActivityKind.Verifying, 3000, 7500),
                new Step(ActivityKind.Moving,    4500, 6000),
            };

        return Enum.TryParse<ActivityKind>(v, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? new[] { new Step(kind, 0, null) }
            : null;
    }

    /// <summary>The kinds that should be active at <paramref name="elapsedMs"/>.</summary>
    public static System.Collections.Generic.IReadOnlyList<ActivityKind> ActiveAt(
        System.Collections.Generic.IReadOnlyList<Step> script, long elapsedMs)
        => script.Where(s => elapsedMs >= s.StartMs && (s.EndMs is null || elapsedMs < s.EndMs)).Select(s => s.Kind).ToList();
}

/// <summary>
/// Plays a <see cref="C64ConsolePreview"/> script into an <see cref="ActivityMonitor"/>, using the shared clock for
/// time (no timer of its own). Begins/ends real scopes, so the console exercises exactly the production path.
/// </summary>
public sealed class ActivityPreviewDriver : IDisposable
{
    private readonly ActivityMonitor _monitor;
    private readonly System.Collections.Generic.IReadOnlyList<C64ConsolePreview.Step> _script;
    private readonly System.Collections.Generic.Dictionary<int, IActivityScope> _live = new();
    private IDisposable? _subscription;
    private readonly long _startMs;
    private readonly ActivityClock _clock;

    public ActivityPreviewDriver(ActivityMonitor monitor, ActivityClock clock, System.Collections.Generic.IReadOnlyList<C64ConsolePreview.Step> script)
    {
        _monitor = monitor; _clock = clock; _script = script;
        _startMs = clock.ElapsedMs;
        Apply(0);
        _subscription = clock.Subscribe(ms => Apply(ms - _startMs));
    }

    /// <summary>Brings the live scopes in line with the script at <paramref name="elapsedMs"/>.</summary>
    public void Apply(long elapsedMs)
    {
        for (int i = 0; i < _script.Count; i++)
        {
            var s = _script[i];
            bool want = elapsedMs >= s.StartMs && (s.EndMs is null || elapsedMs < s.EndMs);
            bool have = _live.ContainsKey(i);
            if (want && !have) _live[i] = _monitor.Begin(s.Kind, "preview");
            else if (!want && have) { _live[i].Dispose(); _live.Remove(i); }
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        foreach (var s in _live.Values) s.Dispose();
        _live.Clear();
    }
}
