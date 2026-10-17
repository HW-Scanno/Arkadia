using System;
using System.IO;
using System.Linq;
using Arkadia.Activities;
using Arkadia.Controls;
using Avalonia.Media;
using Xunit;

namespace Arkadia.Tests.Controls;

/// <summary>
/// CLEAN-EMPTY-FOLDERS-1: the two new console states — CLEANING (sweeping bar clearing dirt pixels) and PURGING
/// (black console, blinking red text, lava region). Pure rules are tested here; the pixels themselves were checked in
/// an isolated app instance.
/// </summary>
public sealed class C64CleaningPurgingTests
{
    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    // ── Catalog ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Labels_AndPatterns()
    {
        Assert.Equal("CLEANING...", ActivityCatalog.Label(ActivityKind.Cleaning));
        Assert.Equal("PURGING...",  ActivityCatalog.Label(ActivityKind.Purging));
        Assert.Equal(ActivityPattern.Sweep, ActivityCatalog.PatternFor(ActivityKind.Cleaning));
        Assert.Equal(ActivityPattern.Lava,  ActivityCatalog.PatternFor(ActivityKind.Purging));
        // no other kind shares the new patterns
        Assert.Equal(new[] { ActivityKind.Cleaning }, Enum.GetValues<ActivityKind>().Where(k => ActivityCatalog.PatternFor(k) == ActivityPattern.Sweep));
        Assert.Equal(new[] { ActivityKind.Purging },  Enum.GetValues<ActivityKind>().Where(k => ActivityCatalog.PatternFor(k) == ActivityPattern.Lava));
    }

    [Fact]
    public void Priority_CleaningIsHousekeeping_BelowRealOperations_PurgingAboveTheCatalogWork()
    {
        Assert.True(ActivityCatalog.Priority(ActivityKind.Cleaning) > ActivityCatalog.Priority(ActivityKind.Loading));
        Assert.True(ActivityCatalog.Priority(ActivityKind.Cleaning) < ActivityCatalog.Priority(ActivityKind.Verifying));
        Assert.True(ActivityCatalog.Priority(ActivityKind.Purging)  > ActivityCatalog.Priority(ActivityKind.Ingesting));
        Assert.True(ActivityCatalog.Priority(ActivityKind.Purging)  < ActivityCatalog.Priority(ActivityKind.Restoring));
    }

    [Fact]
    public void BothLabels_FitTheScreen_OnTheSameGridRuleAsEveryOtherKind()
    {
        double screen = C64ConsoleVisuals.DefaultWidth - 2 * C64ConsoleVisuals.FrameThickness;
        foreach (var k in new[] { ActivityKind.Cleaning, ActivityKind.Purging })
        {
            var text = C64ConsoleVisuals.TextFor(k);
            var size = C64ConsoleVisuals.FontSizeFor(text, screen);
            Assert.True(text.Length * size <= screen);
        }
        Assert.Equal(C64ConsoleVisuals.LargeFontSize, C64ConsoleVisuals.FontSizeFor("PURGING...", screen));    // 10 chars: 2x
        Assert.Equal(C64ConsoleVisuals.SmallFontSize, C64ConsoleVisuals.FontSizeFor("CLEANING...", screen));   // 11 chars: 1x (like INGESTING...)
    }

    [Fact]
    public void Purging_IsConnectedOnlyToTheDestructiveFileDeletionServices()
    {
        var offenders = Directory.EnumerateFiles(RepoFile("."), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && !f.Contains(@"\Arkadia.Tests\"))
            .Where(f => File.ReadAllText(f).Contains("ActivityKind.Purging"))
            .Select(Path.GetFileName)
            .OrderBy(x => x)
            .ToList();
        // the catalog maps it; only the two services that really DELETE files register it (Purge Release + its bulk form)
        Assert.Equal(new[] { "ActivityKind.cs", "LibraryBulkOperationService.cs", "PurgeReleaseService.cs" }, offenders);
    }

    // ── Blink ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Purging_TextBlinks_LikeLoading_AndIsSteadyWithReducedMotion()
    {
        Assert.True(C64ConsoleVisuals.TextVisible(ActivityKind.Purging, 0, false));
        Assert.True(C64ConsoleVisuals.TextVisible(ActivityKind.Purging, 599, false));
        Assert.False(C64ConsoleVisuals.TextVisible(ActivityKind.Purging, 700, false));
        Assert.True(C64ConsoleVisuals.TextVisible(ActivityKind.Purging, 1000, false));
        Assert.True(Enumerable.Range(0, 2000).All(ms => C64ConsoleVisuals.TextVisible(ActivityKind.Purging, ms, true)));
    }

    [Fact]
    public void Cleaning_TextIsSteady()
        => Assert.True(Enumerable.Range(0, 2000).All(ms => C64ConsoleVisuals.TextVisible(ActivityKind.Cleaning, ms, false)));

    // ── Sweep ────────────────────────────────────────────────────────────────────

    private const int W = 180;

    [Fact]
    public void Sweep_IsDeterministic_AndFrameDriven()
    {
        int n = C64ConsolePatterns.SweepCols(W);
        for (long f = 0; f < 200; f += 7)
            for (int strip = 0; strip < 2; strip++)
                for (int c = 0; c < n; c += 5)
                    Assert.Equal(C64ConsolePatterns.SweepLevel(c, 1, f, n, strip), C64ConsolePatterns.SweepLevel(c, 1, f, n, strip));
        Assert.Equal(W / C64ConsolePatterns.SweepCell, n);
    }

    [Fact]
    public void Sweep_BarCrossesTheStrip_ClearingEverythingBehindIt_AndLeavingDirtAhead()
    {
        int n = C64ConsolePatterns.SweepCols(W);
        long mid = 20;                                                        // bar well inside the strip
        int head = (int)mid * C64ConsolePatterns.SweepStepCells;
        for (int row = 0; row < C64ConsolePatterns.SweepRows; row++)
        {
            Assert.Equal(2, C64ConsolePatterns.SweepLevel(head, row, mid, n, 0));
            for (int c = 0; c < head - C64ConsolePatterns.SweepStepCells; c++)
                Assert.Equal(0, C64ConsolePatterns.SweepLevel(c, row, mid, n, 0));            // swept: clear
        }
        int dirtAhead = Enumerable.Range(head + 1, n - head - 1)
            .Sum(c => Enumerable.Range(0, C64ConsolePatterns.SweepRows).Count(r => C64ConsolePatterns.SweepLevel(c, r, mid, n, 0) == 1));
        Assert.True(dirtAhead > 0);
    }

    [Fact]
    public void Sweep_FirstFrame_ShowsDirtAndTheBarAtTheStart_ThisIsTheReducedMotionPicture()
    {
        int n = C64ConsolePatterns.SweepCols(W);
        var cells = Enumerable.Range(0, n).SelectMany(c => Enumerable.Range(0, C64ConsolePatterns.SweepRows).Select(r => C64ConsolePatterns.SweepLevel(c, r, 0, n, 0))).ToList();
        Assert.Contains(2, cells);
        Assert.True(cells.Count(l => l == 1) > n / 2);
        double dirtShare = cells.Count(l => l == 1) / (double)cells.Count;
        Assert.InRange(dirtShare, 0.35, 0.65);                                // ~55 % dirt
    }

    [Fact]
    public void Sweep_EndsClean_RestsThenStartsANewCycleWithFreshDirt()
    {
        int n = C64ConsolePatterns.SweepCols(W);
        long period = C64ConsolePatterns.SweepPeriod(n);
        long rest = period - 2;                                               // bar has left, strip is clean
        for (int c = 0; c < n; c++)
            for (int r = 0; r < C64ConsolePatterns.SweepRows; r++)
                Assert.Equal(0, C64ConsolePatterns.SweepLevel(c, r, rest, n, 0));

        var cycle0 = Enumerable.Range(0, n).Select(c => C64ConsolePatterns.SweepLevel(c, 0, 1, n, 0)).ToList();
        var cycle1 = Enumerable.Range(0, n).Select(c => C64ConsolePatterns.SweepLevel(c, 0, period + 1, n, 0)).ToList();
        Assert.NotEqual(cycle0, cycle1);                                      // different dirt
        Assert.Equal(cycle0.Count(l => l == 2), cycle1.Count(l => l == 2));    // same bar
    }

    [Fact]
    public void Sweep_BottomStripRunsTheOtherWay()
    {
        int n = C64ConsolePatterns.SweepCols(W);
        long f = 15;
        int headTop = Enumerable.Range(0, n).First(c => C64ConsolePatterns.SweepLevel(c, 0, f, n, 0) == 2);
        int headBottom = Enumerable.Range(0, n).First(c => C64ConsolePatterns.SweepLevel(c, 0, f, n, 1) == 2);
        Assert.True(headTop < n / 2 && headBottom > n / 2);
    }

    [Fact]
    public void Sweep_HandlesDegenerateWidths()
    {
        Assert.Equal(0, C64ConsolePatterns.SweepCols(0));
        Assert.Equal(0, C64ConsolePatterns.SweepLevel(0, 0, 10, 0, 0));
        Assert.True(C64ConsolePatterns.SweepPeriod(1) > 0);
    }

    // ── Lava ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lava_LevelsStayInRange_AndAreDeterministic()
    {
        int cols = C64ConsolePatterns.LavaCols(W - 12);
        for (long f = 0; f < 400; f += 3)
            for (int r = 0; r < C64ConsolePatterns.LavaRows; r++)
                for (int c = 0; c < cols; c += 2)
                {
                    int v = C64ConsolePatterns.LavaLevel(c, r, f);
                    Assert.InRange(v, 0, C64ConsolePatterns.LavaLevels - 1);
                    Assert.Equal(v, C64ConsolePatterns.LavaLevel(c, r, f));
                }
    }

    [Fact]
    public void Lava_UsesAllHeatLevels_AndAnyFrameLooksLikeLava_NotAFlatColour()
    {
        int cols = C64ConsolePatterns.LavaCols(W - 12);
        var all = Enumerable.Range(0, 300).SelectMany(f => Enumerable.Range(0, cols).SelectMany(c =>
            Enumerable.Range(0, C64ConsolePatterns.LavaRows).Select(r => C64ConsolePatterns.LavaLevel(c, r, f)))).Distinct().OrderBy(x => x);
        Assert.Equal(Enumerable.Range(0, C64ConsolePatterns.LavaLevels), all);

        // the reduced-motion picture (frame 0) already has several levels, so it is a readable static lava field
        var still = Enumerable.Range(0, cols).SelectMany(c => Enumerable.Range(0, C64ConsolePatterns.LavaRows).Select(r => C64ConsolePatterns.LavaLevel(c, r, 0))).Distinct().Count();
        Assert.True(still >= 3);
    }

    [Fact]
    public void Lava_Flows_FramesDiffer_AndAdjacentFramesChangeOnlyGradually()
    {
        int cols = C64ConsolePatterns.LavaCols(W - 12);
        int Differing(long a, long b) => Enumerable.Range(0, cols).Sum(c => Enumerable.Range(0, C64ConsolePatterns.LavaRows)
            .Count(r => C64ConsolePatterns.LavaLevel(c, r, a) != C64ConsolePatterns.LavaLevel(c, r, b)));
        int total = cols * C64ConsolePatterns.LavaRows;
        Assert.True(Differing(0, 40) > total / 5);                            // clearly animated over time
        Assert.True(Differing(10, 11) < total / 3);                           // but not flickering between frames
    }

    [Fact]
    public void LavaRegion_FitsBesideTheText_InsideTheScreen()
    {
        double screenH = C64ConsoleVisuals.DefaultHeight - 2 * C64ConsoleVisuals.FrameThickness;      // 28
        Assert.True(C64ConsolePatterns.LavaHeight + 2 + C64ConsoleVisuals.LargeFontSize <= screenH);   // lava + gap + 16 px text
    }

    // ── Palette: red/black is PURGING only ───────────────────────────────────────

    [Fact]
    public void LavaPalette_IsRedDarkRedAndOrange_CoolestFirst()
    {
        var p = C64ConsoleVisuals.Lava;
        Assert.Equal(C64ConsolePatterns.LavaLevels, p.Length);
        Assert.All(p, c => Assert.True(c.R >= 0x5A && c.R >= c.G && c.B < 0x20));                        // red-family, no blue
        Assert.True(p[0].R < p[1].R && p[1].R < p[2].R);                                               // dark red → red → bright red
        Assert.True(p[3].G > p[2].G && p[4].G > p[3].G);                                               // → orange → hot
        Assert.Equal(Color.Parse("#000000"), C64ConsoleVisuals.PurgeBlack);
        Assert.True(C64ConsoleVisuals.PurgeRed.R > 0xE0 && C64ConsoleVisuals.PurgeRed.G < 0x60 && C64ConsoleVisuals.PurgeRed.B < 0x60);
    }

    [Fact]
    public void RedBlackStyling_IsConfinedToThePurgingBranch_OtherStatesKeepTheBlueLook()
    {
        var src = File.ReadAllText(RepoFile("Controls/C64StatusConsole.cs")).Replace("\r\n", "\n");
        var render = src.Substring(src.IndexOf("public override void Render(", StringComparison.Ordinal));
        render = render.Substring(0, render.IndexOf("private static void DrawRaster", StringComparison.Ordinal));
        // every use of the black/red brushes in Render is guarded by the lava flag
        Assert.Contains("lava ? BlackBrush : BlueBrush", render);
        Assert.Contains("lava ? PurgeRedBrush : LightBlueBrush", render);
        Assert.Contains("if (lava)", render);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(render, @"PurgeEdgeBrush"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(render, @"BlackBrush").Count);    // outline fill (lava branch) + screen choice
        // blue defaults untouched
        Assert.Equal(Color.Parse("#40318D"), C64ConsoleVisuals.Blue);
    }

    [Fact]
    public void NoImageAssetsOrExtraTimers_WereIntroducedForTheNewStates()
    {
        var src = File.ReadAllText(RepoFile("Controls/C64StatusConsole.cs")) + File.ReadAllText(RepoFile("Controls/C64ConsolePatterns.cs"));
        foreach (var banned in new[] { "Bitmap", "DispatcherTimer", "System.Threading.Timer", "Task.Delay", "Stopwatch.StartNew" })
            Assert.DoesNotContain(banned, src);
    }

    // ── Clock subscription lifecycle ─────────────────────────────────────────────

    private sealed class FakeSource : IActivityTickSource
    {
        public int StartCalls, StopCalls;
        private Action? _tick;
        public void Start(TimeSpan interval, Action tick) { StartCalls++; _tick = tick; }
        public void Stop() { StopCalls++; _tick = null; }
        public void Tick() => _tick?.Invoke();
    }

    [Theory]
    [InlineData(ActivityKind.Cleaning)]
    [InlineData(ActivityKind.Purging)]
    public void NewKinds_UseTheSharedClock_AndReleaseItWhenReadyOrDetached(ActivityKind kind)
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        var c = new C64StatusConsole { Clock = clock };
        c.SetAttached(true);
        c.Kind = kind;
        Assert.True(c.HasClockSubscription);
        Assert.Equal(1, clock.SubscriberCount);
        c.Kind = null;                                                        // back to READY
        Assert.False(c.HasClockSubscription);
        Assert.Equal(0, clock.SubscriberCount);
        Assert.Equal(1, src.StopCalls);                                       // no timer left running
    }

    [Theory]
    [InlineData(ActivityKind.Cleaning)]
    [InlineData(ActivityKind.Purging)]
    public void NewKinds_WithReducedMotion_NeverSubscribe_StaticPresentation(ActivityKind kind)
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, true);
        var c = new C64StatusConsole { Clock = clock };
        c.SetAttached(true);
        c.Kind = kind;
        Assert.False(c.HasClockSubscription);
        Assert.Equal(0, src.StartCalls);
    }

    [Fact]
    public void Preview_CanHoldTheNewKinds_AndCycleIncludesThem()
    {
        foreach (var name in new[] { "cleaning", "purging" })
        {
            var script = C64ConsolePreview.Parse(name)!;
            Assert.Single(C64ConsolePreview.ActiveAt(script, 0));
        }
        var cycle = C64ConsolePreview.Parse("cycle")!;
        Assert.Contains(cycle, s => s.Kind == ActivityKind.Cleaning);
        Assert.Contains(cycle, s => s.Kind == ActivityKind.Purging);
    }
}
