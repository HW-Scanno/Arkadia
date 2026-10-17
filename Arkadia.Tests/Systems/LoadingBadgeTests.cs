using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Controls;
using Arkadia.Systems;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// The LOADING badge must be the standard Arkadia status pill (same shape as ARCHIVED / NEEDS VOLUME), differing only in
/// colour, with opacity-only blinking driven by the shared ActivityClock.
/// </summary>
public sealed class LoadingBadgeTests
{
    private sealed class FakeSource : IActivityTickSource
    {
        public int StartCalls;
        public bool Running;
        private Action? _tick;
        public void Start(TimeSpan interval, Action tick) { StartCalls++; Running = true; _tick = tick; }
        public void Stop() { Running = false; _tick = null; }
        public void Tick() => _tick?.Invoke();
    }

    private static Border Archived() => StatusBadge.Create("ARCHIVED", StorageBadgePolicy.ArchivedColor, StorageBadgePolicy.Tooltip(ArchiveLifecycle.Archived));
    private static Border NeedsVolume() => StatusBadge.Create("NEEDS VOLUME", StorageBadgePolicy.NeedsVolumeColor, null);

    private static TextBlock Text(Border b) => (TextBlock)b.Child!;

    // ── Identical shape to the existing badges ──────────────────────────────────

    [Fact]
    public void LoadingBadge_HasExactlyTheSameShapeAsArchivedAndNeedsVolume()
    {
        var loading = StatusBadge.CreateLoading(new ActivityClock(new FakeSource(), false));

        foreach (var other in new[] { Archived(), NeedsVolume() })
        {
            Assert.Equal(other.CornerRadius,      loading.CornerRadius);        // same corner radius
            Assert.Equal(other.Padding,           loading.Padding);             // same padding
            Assert.Equal(other.BorderThickness,   loading.BorderThickness);     // same border thickness
            Assert.Equal(other.VerticalAlignment, loading.VerticalAlignment);   // same alignment
            Assert.Equal(other.HorizontalAlignment, loading.HorizontalAlignment);
            Assert.Equal(other.Margin,            loading.Margin);
            Assert.Equal(other.MinHeight,         loading.MinHeight);
            Assert.Equal(other.Height.Equals(double.NaN), loading.Height.Equals(double.NaN));   // no fixed height on either
            Assert.Equal(other.GetType(),         loading.GetType());           // a Border, like the others — not a new shape

            var a = Text(other); var l = Text(loading);
            Assert.Equal(a.FontSize,          l.FontSize);                      // same typography
            Assert.Equal(a.FontWeight,        l.FontWeight);
            Assert.Equal(a.FontFamily,        l.FontFamily);
            Assert.Equal(a.FontStyle,         l.FontStyle);
            Assert.Equal(a.LetterSpacing,     l.LetterSpacing);
            Assert.Equal(a.VerticalAlignment, l.VerticalAlignment);
            Assert.Equal(a.TextWrapping,      l.TextWrapping);
        }
    }

    [Fact]
    public void ShapeMetrics_ArePinnedToTheValuesTheBadgesAlwaysHad()
    {
        var b = Archived();
        Assert.Equal(new CornerRadius(3), b.CornerRadius);
        Assert.Equal(new Thickness(6, 1), b.Padding);
        Assert.Equal(new Thickness(1), b.BorderThickness);
        Assert.Equal(9, Text(b).FontSize);
        Assert.Equal(FontWeight.SemiBold, Text(b).FontWeight);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, b.VerticalAlignment);
    }

    [Fact]
    public void OnlyTheColourAndTextDiffer_NeutralWhite_WithTheStandardTintedFill()
    {
        var loading = StatusBadge.CreateLoading(new ActivityClock(new FakeSource(), false));
        var light = Color.Parse(StorageBadgePolicy.LoadingColor);

        // light neutral: bright and desaturated
        Assert.True(light.R >= 0xDD && light.G >= 0xDD && light.B >= 0xDD);
        Assert.True(Math.Max(light.R, Math.Max(light.G, light.B)) - Math.Min(light.R, Math.Min(light.G, light.B)) <= 0x10);

        Assert.Equal("LOADING", Text(loading).Text);
        Assert.Equal(light, ((ISolidColorBrush)loading.BorderBrush!).Color);                 // border and text share the colour
        Assert.Equal(light, ((ISolidColorBrush)Text(loading).Foreground!).Color);
        var fill = ((ISolidColorBrush)loading.Background!).Color;
        Assert.Equal(0x22, fill.A);                                                            // the same 0x22-alpha tint as the others
        Assert.Equal((light.R, light.G, light.B), (fill.R, fill.G, fill.B));
    }

    [Fact]
    public void ExistingBadgesAreUnchanged_StillBuiltByTheSameFactory()
    {
        var a = Archived();
        Assert.Equal(Color.Parse("#4CAF50"), ((ISolidColorBrush)a.BorderBrush!).Color);
        Assert.Equal("ARCHIVED", Text(a).Text);
        Assert.Equal(0x22, ((ISolidColorBrush)a.Background!).Color.A);
        Assert.NotNull(ToolTip.GetTip(a));

        var code = File.ReadAllText(RepoFile("MainWindow.axaml.cs"));
        Assert.Contains("=> Systems.StatusBadge.Create(text, hex, tooltip);", code);          // MainWindow no longer owns a second copy
        Assert.DoesNotContain("CornerRadius      = new Avalonia.CornerRadius(3)", code);
    }

    [Fact]
    public void LoadingBadge_HasALabelColourAndTooltipInTheBadgePolicy()
    {
        Assert.Equal("LOADING", StorageBadgePolicy.LoadingLabel);
        Assert.False(string.IsNullOrWhiteSpace(StorageBadgePolicy.LoadingTooltip));
        var loading = StatusBadge.CreateLoading(new ActivityClock(new FakeSource(), false));
        Assert.Equal(StorageBadgePolicy.LoadingTooltip, ToolTip.GetTip(loading));
    }

    // ── Blinking: opacity only, shared clock, synchronized ──────────────────────

    [Fact]
    public void BlinkPhase_IsTheSameAsTheConsoleLoadingText()
    {
        for (long ms = 0; ms < 3000; ms += 7)
            Assert.Equal(
                C64ConsoleVisuals.TextVisible(Arkadia.Activities.ActivityKind.Loading, ms, false),
                ActivityBlink.OpacityAt(ms, false) == ActivityBlink.OnOpacity);
        Assert.Equal(ActivityBlink.OnOpacity,  ActivityBlink.OpacityAt(0, false));
        Assert.Equal(ActivityBlink.OnOpacity,  ActivityBlink.OpacityAt(599, false));
        Assert.Equal(ActivityBlink.OffOpacity, ActivityBlink.OpacityAt(600, false));
        Assert.Equal(ActivityBlink.OnOpacity,  ActivityBlink.OpacityAt(1000, false));
        Assert.InRange(ActivityBlink.OffOpacity, 0.05, 0.5);                                   // faint, restrained
    }

    [Fact]
    public void ReducedMotion_StaysFullyOpaque_AndNeverSubscribes()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, reducedMotion: true);
        var pill = StatusBadge.CreateLoading(clock);
        var blink = ActivityBlink.Attach(new Border(), clock);
        blink.SetAttached(true);
        Assert.False(blink.IsSubscribed);
        Assert.Equal(0, src.StartCalls);
        Assert.Equal(1.0, pill.Opacity);
        Assert.Equal(1.0, ActivityBlink.OpacityAt(700, reducedMotion: true));
    }

    [Fact]
    public void Blinking_ChangesOpacityAndNothingElse()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        var pill = new Border { Child = new TextBlock { Text = "LOADING" }, Padding = new Thickness(6, 1) };
        var blink = ActivityBlink.Attach(pill, clock);

        var changed = new HashSet<string>();
        pill.PropertyChanged += (_, e) => changed.Add(e.Property.Name);
        var before = (pill.Width, pill.Height, pill.Margin, pill.Padding, pill.BorderThickness, pill.IsVisible, pill.HorizontalAlignment);

        blink.SetAttached(true);
        for (int i = 0; i < 40; i++) { System.Threading.Thread.Sleep(30); src.Tick(); }       // > 1 s of real shared time: crosses on and off phases

        Assert.Equal(new[] { "Opacity" }, changed.ToArray());                                  // ONLY opacity ever changed
        Assert.Equal(before, (pill.Width, pill.Height, pill.Margin, pill.Padding, pill.BorderThickness, pill.IsVisible, pill.HorizontalAlignment));
    }

    [Fact]
    public void WholeBadge_BlinksTogether_BecauseOpacityIsAppliedToTheBorderItself()
    {
        // Opacity on the Border composites its child, fill and border as one: text, border and background fade together.
        var pill = StatusBadge.CreateLoading(new ActivityClock(new FakeSource(), false));
        Assert.Equal(1.0, pill.Opacity);
        Assert.Equal(1.0, Text(pill).Opacity);                                                  // the child is never faded separately
        var src = File.ReadAllText(RepoFile("Controls/ActivityBlink.cs"));
        Assert.Contains("_control.Opacity = o;", src);
        Assert.DoesNotContain("Child", src);
    }

    [Fact]
    public void Badges_ShareTheOneClock_AndSubscribeOnlyWhileAttached()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        var pills = Enumerable.Range(0, 6).Select(_ => new Border()).ToList();
        var blinks = pills.Select(p => ActivityBlink.Attach(p, clock)).ToList();

        Assert.Equal(0, clock.SubscriberCount);                                                 // not on screen: no timer, no work
        Assert.False(clock.IsRunning);

        blinks.ForEach(b => b.SetAttached(true));
        Assert.Equal(6, clock.SubscriberCount);
        Assert.Equal(1, src.StartCalls);                                                        // six LOADING badges, ONE timer

        blinks.ForEach(b => b.SetAttached(false));                                              // rows rebuilt / removed
        Assert.Equal(0, clock.SubscriberCount);
        Assert.False(src.Running);
        Assert.All(pills, p => Assert.Equal(1.0, p.Opacity));                                   // left opaque, never stuck faded
    }

    [Fact]
    public void AllBadgesAtAnyInstant_ShowTheSameOpacity()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        var pills = Enumerable.Range(0, 4).Select(_ => new Border()).ToList();
        var blinks = pills.Select(p => ActivityBlink.Attach(p, clock)).ToList();

        blinks[0].SetAttached(true);
        System.Threading.Thread.Sleep(40);
        blinks[1].SetAttached(true);                                                            // a late joiner mid-cycle
        System.Threading.Thread.Sleep(40);
        blinks[2].SetAttached(true);
        blinks[3].SetAttached(true);

        for (int i = 0; i < 30; i++)
        {
            System.Threading.Thread.Sleep(35);
            src.Tick();
            Assert.Single(pills.Select(p => p.Opacity).Distinct());                             // synchronized, tick by tick
        }
    }

    // ── Integration in the Systems view ─────────────────────────────────────────

    [Fact]
    public void PendingCoverage_ShowsTheLoadingBadge_NotTheSpinner()
    {
        var code = File.ReadAllText(RepoFile("MainWindow.axaml.cs"));
        Assert.Contains("return Systems.StatusBadge.CreateLoading();", code);
        Assert.DoesNotContain("new Systems.PendingSpinner", code);
        // failed and resolved states are untouched
        Assert.Contains("Status calculation failed. Select this system to retry.", code);
        Assert.Contains("Systems.SystemsCoverageColorPolicy.HexFor(percent)", code);
    }

    [Fact]
    public void LoadingBadge_UsesOnlyTheSharedClock_NoTimerOrAnimationOfItsOwn()
    {
        foreach (var f in new[] { "Systems/StatusBadge.cs", "Controls/ActivityBlink.cs" })
        {
            var src = File.ReadAllText(RepoFile(f));
            Assert.DoesNotContain("DispatcherTimer", src);
            Assert.DoesNotContain("Animation", src.Replace("animation", ""));
            Assert.DoesNotContain("new Timer", src);
        }
        Assert.Contains("ActivityClock.Shared", File.ReadAllText(RepoFile("Controls/ActivityBlink.cs")));
    }

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }
}
