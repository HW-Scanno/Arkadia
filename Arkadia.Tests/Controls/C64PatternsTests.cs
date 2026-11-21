using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Arkadia.Activities;
using Arkadia.Controls;
using Xunit;

namespace Arkadia.Tests.Controls;

/// <summary>C64-ACTIVITY-1: procedural pattern rules, label fitting, the single shared phase, and the preview script.</summary>
public sealed class C64PatternsTests
{
    private const int Width = 180;

    // ── Block flow ──────────────────────────────────────────────────────────────

    [Fact]
    public void BlockFlow_DriftsOnePixelPerFrame_LeftToRight_AndWraps()
    {
        for (long f = 0; f < 64; f++)
        {
            int a = C64ConsolePatterns.FlowOffset(f, 0), b = C64ConsolePatterns.FlowOffset(f + 1, 0);
            Assert.InRange(a, 0, C64ConsolePatterns.FlowPeriod - 1);
            Assert.Equal((a + 1) % C64ConsolePatterns.FlowPeriod, b);          // +1 px right per frame
        }
    }

    [Fact]
    public void BlockFlow_BottomStripIsStaggeredByHalfAPeriod()
    {
        for (long f = 0; f < 16; f++)
            Assert.Equal((C64ConsolePatterns.FlowOffset(f, 0) + C64ConsolePatterns.FlowPeriod / 2) % C64ConsolePatterns.FlowPeriod,
                         C64ConsolePatterns.FlowOffset(f, 1));
    }

    // ── Columns ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Columns_HeightsFluctuateWithinTheStrip_AndAreDeterministic()
    {
        int n = C64ConsolePatterns.ColumnCount(Width);
        Assert.Equal(60, n);
        var seen = new HashSet<int>();
        for (long f = 0; f < 120; f++)
            for (int i = 0; i < n; i++)
            {
                int h = C64ConsolePatterns.ColumnHeight(i, f, 0);
                Assert.InRange(h, 1, C64ConsolePatterns.ColumnMaxHeight);
                Assert.Equal(h, C64ConsolePatterns.ColumnHeight(i, f, 0));       // pure function of (index, frame)
                seen.Add(h);
            }
        Assert.True(seen.Count >= 5);                                            // genuinely fluctuating
    }

    [Fact]
    public void Columns_HoldTheirHeightForAFewFrames_NotEveryFrame()
    {
        // restrained: a column changes at most once per ColumnHoldFrames frames
        for (int i = 0; i < 60; i++)
            for (long f = 0; f < 90; f += C64ConsolePatterns.ColumnHoldFrames)
                for (int k = 1; k < C64ConsolePatterns.ColumnHoldFrames; k++)
                    Assert.Equal(C64ConsolePatterns.ColumnHeight(i, f, 0), C64ConsolePatterns.ColumnHeight(i, f + k, 0));
    }

    // ── Hex stream ──────────────────────────────────────────────────────────────

    [Fact]
    public void HexGlyphs_AreSixteenDistinct3x5Pixelmaps()
    {
        var maps = Enumerable.Range(0, 16).Select(v =>
            string.Concat(Enumerable.Range(0, 5).SelectMany(r => Enumerable.Range(0, 3).Select(c => C64ConsolePatterns.HexPixel(v, c, r) ? '#' : '.')))).ToList();
        Assert.Equal(16, maps.Distinct().Count());
        Assert.All(maps, m => Assert.InRange(m.Count(ch => ch == '#'), 5, 13));
        // spot checks: '0' is a ring, '1' has a full base, '8' is full-height with a middle bar
        Assert.False(C64ConsolePatterns.HexPixel(0, 1, 2));
        Assert.True(C64ConsolePatterns.HexPixel(0, 0, 2) && C64ConsolePatterns.HexPixel(0, 2, 2));
        Assert.True(Enumerable.Range(0, 3).All(c => C64ConsolePatterns.HexPixel(1, c, 4)));
        Assert.True(Enumerable.Range(0, 3).All(c => C64ConsolePatterns.HexPixel(8, c, 2)));
    }

    [Fact]
    public void HexStream_ValuesStayInRange_AndOnlyAFractionOfColumnsChangePerFrame()
    {
        int n = C64ConsolePatterns.HexCount(Width);
        Assert.Equal(45, n);
        long changed = 0, total = 0;
        for (long f = 0; f < 120; f++)
            for (int i = 0; i < n; i++)
            {
                int a = C64ConsolePatterns.HexValue(i, f, 0), b = C64ConsolePatterns.HexValue(i, f + 1, 0);
                Assert.InRange(a, 0, 15);
                total++;
                if (a != b) changed++;
            }
        Assert.InRange(changed / (double)total, 0.05, 0.30);                      // staggered, restrained (≈ 1/6 re-roll per frame)
    }

    // ── Scanner ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Scanner_HeadPingPongsAcrossEveryBlock_AndFlipsDirectionAtTheEnds()
    {
        int n = C64ConsolePatterns.ScanCount(Width);
        Assert.Equal(30, n);
        var positions = new List<int>();
        var dirs = new List<int>();
        for (long f = 0; f < 2 * (n - 1); f++)
        {
            var (p, d) = C64ConsolePatterns.ScanHead(f, n);
            Assert.InRange(p, 0, n - 1);
            positions.Add(p); dirs.Add(d);
        }
        Assert.Equal(Enumerable.Range(0, n), positions.Distinct().OrderBy(x => x));             // visits every block
        Assert.Equal((0, 1), C64ConsolePatterns.ScanHead(0, n));
        Assert.Equal((n - 1, 1), C64ConsolePatterns.ScanHead(n - 1, n));
        Assert.Equal((n - 2, -1), C64ConsolePatterns.ScanHead(n, n));                           // returns leftwards
        Assert.Equal(C64ConsolePatterns.ScanHead(0, n), C64ConsolePatterns.ScanHead(2 * (n - 1), n));   // periodic
        for (int i = 1; i < positions.Count; i++) Assert.Equal(1, Math.Abs(positions[i] - positions[i - 1]));   // one block per frame
    }

    [Fact]
    public void Scanner_LevelsFormAHeadWithAFadingTrailBehindIt()
    {
        // moving right: head at 10 → 10 is bright, 9 and 8 fade, everything else (incl. ahead) is idle
        Assert.Equal(new[] { 0, 1, 2, 3, 0 }, new[] { 7, 8, 9, 10, 11 }.Select(i => C64ConsolePatterns.ScanLevel(i, 10, +1)));
        // moving left: the trail is on the right
        Assert.Equal(new[] { 0, 3, 2, 1, 0 }, new[] { 9, 10, 11, 12, 13 }.Select(i => C64ConsolePatterns.ScanLevel(i, 10, -1)).Select((v, k) => k == 0 ? 0 : v));
    }

    // ── Geometry fits the 6 px strips; hash is deterministic ────────────────────

    [Fact]
    public void PatternGeometry_FitsInsideTheBorderStrips()
    {
        int strip = C64ConsoleVisuals.FrameThickness;
        Assert.True(1 + C64ConsolePatterns.FlowBlock <= strip);
        Assert.True(1 + C64ConsolePatterns.ScanBlock <= strip);
        Assert.True(1 + C64ConsolePatterns.HexRowsPerGlyph <= strip);
        Assert.True(C64ConsolePatterns.ColumnMaxHeight <= strip);
        Assert.True(C64ConsolePatterns.HexCols < C64ConsolePatterns.HexPitch);                  // 1 px gap between glyphs
        Assert.True(C64ConsolePatterns.ColumnWidth < C64ConsolePatterns.ColumnPitch);
        Assert.True(C64ConsolePatterns.ScanBlock < C64ConsolePatterns.ScanPitch);
    }

    [Fact]
    public void Hash_IsDeterministic_AndSpreadsValues()
    {
        Assert.Equal(C64ConsolePatterns.Hash(7, 99), C64ConsolePatterns.Hash(7, 99));
        Assert.NotEqual(C64ConsolePatterns.Hash(7, 99), C64ConsolePatterns.Hash(8, 99));
        Assert.True(Enumerable.Range(0, 200).Select(i => C64ConsolePatterns.Hash(i, 5) % 16).Distinct().Count() >= 14);
    }

    // ── Text fits the console for EVERY kind (never clips) ──────────────────────

    [Fact]
    public void EveryLabel_FitsTheScreen_AtItsChosenGridSize()
    {
        double screen = C64ConsoleVisuals.DefaultWidth - 2 * C64ConsoleVisuals.FrameThickness;      // 168
        foreach (var kind in Enum.GetValues<ActivityKind>())
        {
            var text = C64ConsoleVisuals.TextFor(kind);
            var size = C64ConsoleVisuals.FontSizeFor(text, screen);
            Assert.True(text.Length * size <= screen, $"{text} at {size}px = {text.Length * size} > {screen}");
            Assert.Contains(size, new[] { C64ConsoleVisuals.LargeFontSize, C64ConsoleVisuals.SmallFontSize });   // always on the 8 px grid
            Assert.True(size <= C64ConsoleVisuals.DefaultHeight - 2 * C64ConsoleVisuals.FrameThickness);        // fits vertically
        }
        Assert.Equal(C64ConsoleVisuals.LargeFontSize, C64ConsoleVisuals.FontSizeFor("LOADING...", screen));
        Assert.Equal(C64ConsoleVisuals.LargeFontSize, C64ConsoleVisuals.FontSizeFor("MOVING...", screen));
        Assert.Equal(C64ConsoleVisuals.SmallFontSize, C64ConsoleVisuals.FontSizeFor("REABSORBING...", screen));
    }

    [Fact]
    public void OnlyLoadingAndPurgingTextBlink_EveryOtherKindIsSteady()
    {
        foreach (var kind in Enum.GetValues<ActivityKind>())
        {
            bool blinks = Enumerable.Range(0, 1000).Any(ms => !C64ConsoleVisuals.TextVisible(kind, ms, false));
            Assert.Equal(kind is ActivityKind.Loading or ActivityKind.Purging, blinks);
            Assert.True(Enumerable.Range(0, 1000).All(ms => C64ConsoleVisuals.TextVisible(kind, ms, true)));   // reduced motion: steady
        }
    }

    [Fact]
    public void RasterIndexAndColour_AreConsistent()
    {
        for (int y = -20; y < 60; y++)
            for (int off = 0; off < 36; off += 5)
                Assert.Equal(C64ConsoleVisuals.Stripes[C64ConsoleVisuals.StripeIndexAt(y, off)], C64ConsoleVisuals.StripeColorAt(y, off));
    }

    // ── One shared phase ────────────────────────────────────────────────────────

    private sealed class FakeSource : IActivityTickSource
    {
        public int StartCalls;
        private Action? _tick;
        public void Start(TimeSpan interval, Action tick) { StartCalls++; _tick = tick; }
        public void Stop() { _tick = null; }
        public void Tick() => _tick?.Invoke();
    }

    [Fact]
    public void Frame_IsAPureQuantizationOfTheSharedTime()
    {
        Assert.Equal(0, ActivityClock.FrameOf(0));
        Assert.Equal(0, ActivityClock.FrameOf(79));
        Assert.Equal(1, ActivityClock.FrameOf(80));
        Assert.Equal(12, ActivityClock.FrameOf(1000));
    }

    [Fact]
    public void Clock_TimebaseIsNeverReset_AcrossStopAndRestart()
    {
        var clock = new ActivityClock(new FakeSource(), false);
        var s1 = clock.Subscribe(_ => { });
        var t1 = clock.ElapsedMs;
        Thread.Sleep(25);
        s1.Dispose();                                            // clock stops (no subscribers)
        Thread.Sleep(25);
        var s2 = clock.Subscribe(_ => { });                      // ... and restarts
        Assert.True(clock.ElapsedMs >= t1 + 45, "the shared phase must keep running across idle periods");
        s2.Dispose();
    }

    [Fact]
    public void LateJoiner_IsImmediatelyInPhase_AndAfterTheNextTickBitIdentical()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        var first = new C64StatusConsole { Clock = clock, Kind = ActivityKind.Verifying };
        first.SetAttached(true);
        Thread.Sleep(30);
        src.Tick();

        Thread.Sleep(30);                                        // a second indicator appears halfway through the cycle
        var second = new C64StatusConsole { Clock = clock, Kind = ActivityKind.Verifying };
        second.SetAttached(true);
        Assert.True(second.PhaseMs >= first.PhaseMs);            // it read the SHARED time, not a private zero
        Assert.True(clock.ElapsedMs - second.PhaseMs < 25);

        Thread.Sleep(10);
        src.Tick();
        Assert.Equal(first.PhaseMs, second.PhaseMs);             // one tick → one phase for everybody
        Assert.Equal(1, src.StartCalls);                         // and still a single timer
    }

    [Fact]
    public void ChangingTheKindWhileAnimating_KeepsOneSubscription_AndResyncs()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        var c = new C64StatusConsole { Clock = clock };
        c.SetAttached(true);

        c.Kind = ActivityKind.Loading;
        c.Kind = ActivityKind.Verifying;
        c.Kind = ActivityKind.Moving;
        Assert.Equal(1, clock.SubscriberCount);
        Assert.Equal(1, src.StartCalls);

        c.Kind = null;
        Assert.Equal(0, clock.SubscriberCount);
        Assert.False(clock.IsRunning);                           // READY: nothing animates
    }

    [Fact]
    public void EveryKind_AnimatesThroughTheSameClock_AndStopsCleanly()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        var c = new C64StatusConsole { Clock = clock };
        c.SetAttached(true);
        foreach (var kind in Enum.GetValues<ActivityKind>().Where(k => ActivityCatalog.PatternFor(k) != ActivityPattern.Static))
        {
            c.Kind = kind;
            Assert.True(c.HasClockSubscription);
        }
        c.SetAttached(false);
        Assert.Equal(0, clock.SubscriberCount);                  // resources released on detach
        Assert.Equal(1, src.StartCalls);
    }

    // ── Preview harness (development only) ──────────────────────────────────────

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("  ")] [InlineData("nonsense")] [InlineData("ready")] [InlineData("999")]
    public void Preview_UnsetOrUnknown_MeansNoPreview(string? value) => Assert.Null(C64ConsolePreview.Parse(value));

    [Theory]
    [InlineData("loading", ActivityKind.Loading)] [InlineData("MOVING", ActivityKind.Moving)] [InlineData(" Verifying ", ActivityKind.Verifying)]
    [InlineData("ingesting", ActivityKind.Ingesting)] [InlineData("importing", ActivityKind.Importing)] [InlineData("reabsorbing", ActivityKind.Reabsorbing)]
    public void Preview_AKindName_HoldsThatActivity(string value, ActivityKind expected)
    {
        var script = C64ConsolePreview.Parse(value)!;
        Assert.Equal(new[] { expected }, C64ConsolePreview.ActiveAt(script, 0));
        Assert.Equal(new[] { expected }, C64ConsolePreview.ActiveAt(script, 10_000_000));
    }

    [Fact]
    public void Preview_Cycle_ShowsEveryKindOnce_ForThreeSecondsEach()
    {
        var script = C64ConsolePreview.Parse("cycle")!;
        Assert.Equal(Enum.GetValues<ActivityKind>().Length, script.Count);
        foreach (var (kind, i) in Enum.GetValues<ActivityKind>().Select((k, i) => (k, i)))
        {
            Assert.Equal(new[] { kind }, C64ConsolePreview.ActiveAt(script, i * 3000L));
            Assert.Equal(new[] { kind }, C64ConsolePreview.ActiveAt(script, i * 3000L + 2999));
        }
        Assert.Empty(C64ConsolePreview.ActiveAt(script, Enum.GetValues<ActivityKind>().Length * 3000L));
    }

    [Fact]
    public void Preview_Driver_PlaysTheOverlapScript_ThroughTheRealMonitor()
    {
        var monitor = new ActivityMonitor();
        var seen = new List<ActivityKind?>();
        monitor.ForegroundChanged += seen.Add;
        var clock = new ActivityClock(new FakeSource(), reducedMotion: true);
        var driver = new ActivityPreviewDriver(monitor, clock, C64ConsolePreview.Parse("overlap")!);

        foreach (var ms in new long[] { 0, 3500, 5000, 6500, 8000, 9500 }) driver.Apply(ms);

        Assert.Equal(new ActivityKind?[]
        {
            ActivityKind.Loading, ActivityKind.Verifying, ActivityKind.Moving,   // Moving (higher priority) joins
            ActivityKind.Verifying,                                              // Moving ends → Verifying
            ActivityKind.Loading,                                                // Verifying ends → Loading
            null,                                                                // Loading ends → READY
        }, seen);
        driver.Dispose();
        Assert.True(monitor.IsIdle);
    }

    [Fact]
    public void Preview_Driver_Dispose_ReleasesEverything()
    {
        var monitor = new ActivityMonitor();
        var clock = new ActivityClock(new FakeSource(), false);
        var driver = new ActivityPreviewDriver(monitor, clock, C64ConsolePreview.Parse("verifying")!);
        Assert.Equal(ActivityKind.Verifying, monitor.Foreground);
        Assert.Equal(1, clock.SubscriberCount);
        driver.Dispose();
        Assert.True(monitor.IsIdle);
        Assert.Equal(0, clock.SubscriberCount);
    }
}
