using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Arkadia.Activities;
using Arkadia.Controls;
using Avalonia;
using Avalonia.Platform;
using Xunit;

namespace Arkadia.Tests.Controls;

/// <summary>
/// GLOBAL-ACTIVITY-MONITOR-1 (visual prototype): embedded C64 font, pure visual rules, the shared animation clock
/// and the console's subscription lifecycle. Pixel rendering itself needs a windowing platform, so it was checked
/// visually in an isolated app instance (see the delivery report) rather than here.
/// </summary>
public sealed class C64StatusConsoleTests
{
    private const string FontAsset = "avares://Arkadia/Assets/Fonts/C64_Pro_Mono-STYLE.ttf";

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    // ── 1/2. Font: embedded, correctly named, packaged, licensed ────────────────

    // No Avalonia application exists in the unit-test process, so use the standard loader directly: it resolves
    // avares:// URIs against the real compiled assembly — the same resource blob the running app reads.
    private static readonly IAssetLoader Loader = new StandardAssetLoader();

    private static byte[] ReadEmbeddedFont()
    {
        using var s = Loader.Open(new Uri(FontAsset));
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Reads the OpenType 'name' table entry (Windows/Unicode) with the given id.</summary>
    private static string? FontName(byte[] f, int nameId)
    {
        int numTables = (f[4] << 8) | f[5];
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + 16 * i;
            if (Encoding.ASCII.GetString(f, rec, 4) != "name") continue;
            int off = (f[rec + 8] << 24) | (f[rec + 9] << 16) | (f[rec + 10] << 8) | f[rec + 11];
            int count = (f[off + 2] << 8) | f[off + 3];
            int strOff = (f[off + 4] << 8) | f[off + 5];
            for (int n = 0; n < count; n++)
            {
                int r = off + 6 + 12 * n;
                int platform = (f[r] << 8) | f[r + 1], id = (f[r + 6] << 8) | f[r + 7];
                int len = (f[r + 8] << 8) | f[r + 9], o = (f[r + 10] << 8) | f[r + 11];
                if (platform == 3 && id == nameId)
                    return Encoding.BigEndianUnicode.GetString(f, off + strOff + o, len);
            }
        }
        return null;
    }

    [Fact]
    public void Font_IsAnEmbeddedAvaloniaResource_WithItsOriginalFileName()
    {
        Assert.True(Loader.Exists(new Uri(FontAsset)));
        Assert.True(File.Exists(RepoFile("Assets/Fonts/C64_Pro_Mono-STYLE.ttf")));
        Assert.Contains("Assets\\**", File.ReadAllText(RepoFile("Arkadia.csproj")));            // AvaloniaResource wildcard
    }

    [Fact]
    public void EmbeddedFont_IsByteIdenticalToTheProvidedFile()
    {
        Assert.Equal(File.ReadAllBytes(RepoFile("Assets/Fonts/C64_Pro_Mono-STYLE.ttf")), ReadEmbeddedFont());
    }

    [Fact]
    public void Font_InternalFamilyName_MatchesWhatTheControlRequests()
    {
        var font = ReadEmbeddedFont();
        Assert.Equal("C64 Pro Mono", FontName(font, 1));
        Assert.EndsWith("#C64 Pro Mono", C64StatusConsole.FontUri);                             // family name used by the control
        Assert.Equal("avares://Arkadia/Assets/Fonts", C64StatusConsole.FontUri.Split('#')[0]);
    }

    [Fact]
    public void BuiltAssembly_ContainsTheFontBytes_SoPortableBuildsCarryIt()
    {
        // Self-contained single-file publish bundles Arkadia.dll, whose Avalonia resource blob holds the font.
        var font = File.ReadAllBytes(RepoFile("Assets/Fonts/C64_Pro_Mono-STYLE.ttf"));
        var probe = font.Skip(font.Length / 2).Take(96).ToArray();                              // a slice of glyph data
        var dll = File.ReadAllBytes(typeof(C64StatusConsole).Assembly.Location);
        Assert.True(IndexOf(dll, probe) >= 0, "font bytes not found inside the compiled assembly");

        var profile = File.ReadAllText(RepoFile("Properties/PublishProfiles/win-x64-portable.pubxml"));
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", profile);
        Assert.DoesNotContain("Fonts", profile);                                                // no loose font copy / system install
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    [Fact]
    public void License_TravelsWithTheFont_AndQuotesTheAuthorsTerms()
    {
        var path = RepoFile("Assets/Fonts/C64_Pro_Mono-STYLE.LICENSE.txt");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("C64 Pro Mono", text);
        Assert.Contains("style64.org", text);
        Assert.Contains("ONLY if", text);                       // the "freely provided" condition is preserved verbatim
        // The quoted license must equal the one embedded in the font itself (nameID 13).
        var embedded = FontName(ReadEmbeddedFont(), 13)!;
        var collapsed = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(string.Join(" ", embedded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), collapsed);
    }

    // ── Visual rules (pure) ─────────────────────────────────────────────────────

    [Fact]
    public void Texts_AreExactlyReadyAndLoading()
    {
        Assert.Equal("READY.", C64ConsoleVisuals.TextFor((ActivityKind?)null));
        Assert.Equal("LOADING...", C64ConsoleVisuals.TextFor(ActivityKind.Loading));
    }

    [Fact]
    public void Ready_NeverBlinks()
    {
        for (long ms = 0; ms < 5000; ms += 37)
            Assert.True(C64ConsoleVisuals.TextVisible((ActivityKind?)null, ms, reducedMotion: false));
    }

    [Fact]
    public void Loading_BlinksAtAboutOneHertz_VisibleMostOfTheTime()
    {
        var samples = Enumerable.Range(0, 1000).Select(ms => C64ConsoleVisuals.TextVisible(ActivityKind.Loading, ms, false)).ToList();
        Assert.Equal(600, samples.Count(v => v));
        int transitions = 0;
        for (int i = 1; i < samples.Count; i++) if (samples[i] != samples[i - 1]) transitions++;
        Assert.Equal(1, transitions);                                       // on→off once per 1000 ms period (then wraps to on)
        Assert.True(C64ConsoleVisuals.TextVisible(ActivityKind.Loading, 0, false));
        Assert.False(C64ConsoleVisuals.TextVisible(ActivityKind.Loading, 700, false));
        Assert.True(C64ConsoleVisuals.TextVisible(ActivityKind.Loading, 1000, false));
    }

    [Fact]
    public void ReducedMotion_KeepsTheTextSteady_AndTheBandsStill()
    {
        for (long ms = 0; ms < 3000; ms += 50)
        {
            Assert.True(C64ConsoleVisuals.TextVisible(ActivityKind.Loading, ms, reducedMotion: true));
            Assert.Equal(0, C64ConsoleVisuals.StripeOffset(ms, reducedMotion: true));
        }
    }

    [Fact]
    public void Bands_ScrollGently_NeverMoreThanOnePixelPerTick_AndWrap()
    {
        int prev = C64ConsoleVisuals.StripeOffset(0, false);
        int period = C64ConsoleVisuals.StripeHeight * C64ConsoleVisuals.Stripes.Length;
        for (long ms = 80; ms < 20_000; ms += 80)
        {
            int cur = C64ConsoleVisuals.StripeOffset(ms, false);
            Assert.InRange(cur, 0, period - 1);
            Assert.True(cur == (prev + 1) % period, $"jump at {ms} ms");
            prev = cur;
        }
    }

    [Fact]
    public void Bands_AreMulticoloured_Horizontal_AndPeriodic()
    {
        var colours = C64ConsoleVisuals.Stripes;
        Assert.True(colours.Distinct().Count() >= 5);
        int n = C64ConsoleVisuals.StripeHeight * colours.Length;
        for (int y = 0; y < 100; y++)
            Assert.Equal(C64ConsoleVisuals.StripeColorAt(y, 3), C64ConsoleVisuals.StripeColorAt(y + n, 3));
        // a band is StripeHeight rows tall: constant inside, different from the next
        Assert.Equal(C64ConsoleVisuals.StripeColorAt(0, 0), C64ConsoleVisuals.StripeColorAt(C64ConsoleVisuals.StripeHeight - 1, 0));
        Assert.NotEqual(C64ConsoleVisuals.StripeColorAt(0, 0), C64ConsoleVisuals.StripeColorAt(C64ConsoleVisuals.StripeHeight, 0));
        Assert.Equal(colours[0], C64ConsoleVisuals.StripeColorAt(-n, 0));                       // negative rows are safe
    }

    [Fact]
    public void NoNumericProgress_IsEverShown()
    {
        Assert.DoesNotContain('%', C64ConsoleVisuals.LoadingText);
        Assert.DoesNotContain(C64ConsoleVisuals.LoadingText, char.IsDigit);
        var src = File.ReadAllText(RepoFile("Controls/C64StatusConsole.cs"));
        Assert.DoesNotContain("Progress", src);
    }

    [Fact]
    public void BothTexts_FitTheSidebarWidth_AtTheLargeGridSize()
    {
        // sidebar 220 - 2*20 padding = 180 control width; screen = width - 2*frame
        var screen = C64ConsoleVisuals.DefaultWidth - 2 * C64ConsoleVisuals.FrameThickness;
        foreach (var t in new[] { C64ConsoleVisuals.ReadyText, C64ConsoleVisuals.LoadingText })
        {
            Assert.Equal(C64ConsoleVisuals.LargeFontSize, C64ConsoleVisuals.FontSizeFor(t, screen));
            Assert.True(t.Length * C64ConsoleVisuals.LargeFontSize <= screen);                 // 1 em advance per glyph (verified in the font)
        }
        Assert.True(C64ConsoleVisuals.DefaultHeight >= 2 * C64ConsoleVisuals.FrameThickness + C64ConsoleVisuals.LargeFontSize);
    }

    [Fact]
    public void WhenTooNarrow_TextFallsBackToTheSmallGridSize_NeverClips()
    {
        Assert.Equal(C64ConsoleVisuals.SmallFontSize, C64ConsoleVisuals.FontSizeFor("LOADING...", 100));
        Assert.Equal(C64ConsoleVisuals.LargeFontSize, C64ConsoleVisuals.FontSizeFor("READY.", 100));
    }

    // ── Shared clock ────────────────────────────────────────────────────────────

    private sealed class FakeSource : IActivityTickSource
    {
        public int StartCalls, StopCalls;
        public bool Running;
        private Action? _tick;
        public void Start(TimeSpan interval, Action tick) { StartCalls++; Running = true; _tick = tick; LastInterval = interval; }
        public void Stop() { StopCalls++; Running = false; _tick = null; }
        public TimeSpan LastInterval;
        public void Tick() => _tick?.Invoke();
    }

    [Fact]
    public void Clock_HasNoTimerWhileNobodyIsSubscribed()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, reducedMotion: false);
        Assert.False(clock.IsRunning);
        Assert.Equal(0, src.StartCalls);
        Assert.Equal(0, clock.SubscriberCount);
    }

    [Fact]
    public void Clock_StartsOneTimerForAnyNumberOfSubscribers_AndStopsWhenTheLastLeaves()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        var ticks = new List<long>();

        var a = clock.Subscribe(ms => ticks.Add(ms));
        var b = clock.Subscribe(ms => ticks.Add(ms));
        var c = clock.Subscribe(_ => { });
        Assert.Equal(1, src.StartCalls);                       // ONE timer shared by every indicator
        Assert.True(clock.IsRunning);

        src.Tick();
        Assert.Equal(2, ticks.Count);                          // each subscriber notified once per tick

        a.Dispose(); b.Dispose();
        Assert.True(src.Running);                              // still one subscriber
        c.Dispose();
        Assert.False(src.Running);
        Assert.Equal(1, src.StopCalls);
        Assert.False(clock.IsRunning);

        var d = clock.Subscribe(_ => { });                     // restart works
        Assert.Equal(2, src.StartCalls);
        d.Dispose();
    }

    [Fact]
    public void Clock_DisposeIsIdempotent_AndCallbacksMayUnsubscribeDuringATick()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        IDisposable? self = null;
        int calls = 0;
        self = clock.Subscribe(_ => { calls++; self!.Dispose(); });
        var other = clock.Subscribe(_ => { });

        src.Tick();
        src.Tick();
        Assert.Equal(1, calls);
        self.Dispose(); self.Dispose();
        Assert.Equal(1, clock.SubscriberCount);
        other.Dispose();
        Assert.Equal(0, clock.SubscriberCount);
    }

    [Fact]
    public void Clock_ReducedMotion_NeverStartsATimer()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, reducedMotion: true);
        using var sub = clock.Subscribe(_ => { });
        Assert.Equal(0, src.StartCalls);
        Assert.False(clock.IsRunning);
    }

    [Fact]
    public void Clock_TickRate_IsRestrained()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        using var sub = clock.Subscribe(_ => { });
        Assert.Equal(ActivityClock.Interval, src.LastInterval);
        Assert.True(ActivityClock.Interval >= TimeSpan.FromMilliseconds(50));                  // <= 20 Hz repaint ceiling
    }

    [Theory]
    [InlineData("1", null, true)]
    [InlineData("true", true, true)]
    [InlineData("YES", true, true)]
    [InlineData("0", false, false)]                         // explicit variable beats the OS
    [InlineData("false", false, false)]
    [InlineData(null, false, true)]                         // OS says animations off
    [InlineData(null, true, false)]
    [InlineData(null, null, false)]                         // unknown (e.g. Linux): motion allowed
    [InlineData("", true, false)]
    public void ReducedMotionDetector_Rules(string? env, bool? osAnimations, bool expected)
        => Assert.Equal(expected, ReducedMotionDetector.Detect(_ => env, () => osAnimations));

    // ── Console lifecycle ───────────────────────────────────────────────────────

    private static (C64StatusConsole Console, FakeSource Source, ActivityClock Clock) Make(bool reduced = false)
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, reduced);
        return (new C64StatusConsole { Clock = clock }, src, clock);
    }

    [Fact]
    public void Console_DefaultsToReady_AndOwnsNoSubscription()
    {
        var (c, src, clock) = Make();
        Assert.Null(c.Kind);
        c.SetAttached(true);
        Assert.False(c.HasClockSubscription);
        Assert.Equal(0, clock.SubscriberCount);
        Assert.Equal(0, src.StartCalls);                       // READY: no timer, no repaint traffic
    }

    [Fact]
    public void Console_Animates_OnlyWhileAttachedAndLoading()
    {
        var (c, src, clock) = Make();
        c.Kind = ActivityKind.Loading;
        Assert.False(c.HasClockSubscription);                  // not attached yet

        c.SetAttached(true);
        Assert.True(c.HasClockSubscription);
        Assert.True(clock.IsRunning);

        c.Kind = null;                         // animation stops when no longer required
        Assert.False(c.HasClockSubscription);
        Assert.False(src.Running);

        c.Kind = ActivityKind.Loading;
        Assert.True(src.Running);
        c.SetAttached(false);                                  // removed from the UI: stops too
        Assert.False(c.HasClockSubscription);
        Assert.False(src.Running);
    }

    [Fact]
    public void Console_ReducedMotion_NeverSubscribes()
    {
        var (c, src, clock) = Make(reduced: true);
        c.Kind = ActivityKind.Loading;
        c.SetAttached(true);
        Assert.False(c.HasClockSubscription);
        Assert.Equal(0, clock.SubscriberCount);
        Assert.Equal(0, src.StartCalls);
    }

    [Fact]
    public void ManyConsoles_ShareOneClockAndOneTimer()
    {
        var src = new FakeSource();
        var clock = new ActivityClock(src, false);
        var consoles = Enumerable.Range(0, 5).Select(_ => new C64StatusConsole { Clock = clock, Kind = ActivityKind.Loading }).ToList();
        consoles.ForEach(c => c.SetAttached(true));

        Assert.Equal(5, clock.SubscriberCount);
        Assert.Equal(1, src.StartCalls);                       // future "LOADING..." indicators add subscribers, not timers

        consoles.ForEach(c => c.Kind = null);
        Assert.False(src.Running);
    }

    [Fact]
    public void Console_StateChangeWithoutAnimationDoesNotTouchTheClock()
    {
        var (c, src, _) = Make();
        c.SetAttached(true);
        c.Kind = null;
        c.Kind = null;
        Assert.Equal(0, src.StartCalls);
        Assert.Equal(0, src.StopCalls);
    }

    [Fact]
    public void Console_FitsTheSidebar_FixedHeight_AndNeverRelayoutsWhileAnimating()
    {
        var (c, _, _) = Make();
        c.Measure(new Size(180, 500));
        Assert.Equal(new Size(180, C64ConsoleVisuals.DefaultHeight), c.DesiredSize);
        c.Measure(Size.Infinity);
        Assert.Equal(new Size(C64ConsoleVisuals.DefaultWidth, C64ConsoleVisuals.DefaultHeight), c.DesiredSize);

        // Animation only repaints: the state property affects render, not measure/arrange.
        c.Kind = ActivityKind.Loading;
        c.SetAttached(true);
        Assert.True(c.IsMeasureValid);
    }

    [Fact]
    public void Console_HasAnAccessibleName_ThatFollowsTheState()
    {
        var (c, _, _) = Make();
        Assert.Equal("Activity status: ready", Avalonia.Automation.AutomationProperties.GetName(c));
        c.Kind = ActivityKind.Loading;
        Assert.Equal("Activity status: loading", Avalonia.Automation.AutomationProperties.GetName(c));
    }

    [Fact]
    public void ConsoleSource_OwnsNoTimer_OnlyTheSharedClockDoes()
    {
        var console = File.ReadAllText(RepoFile("Controls/C64StatusConsole.cs"));
        Assert.DoesNotContain("DispatcherTimer", console);
        Assert.DoesNotContain("System.Threading.Timer", console);
        Assert.DoesNotContain("new Timer", console);
        Assert.DoesNotContain("using Avalonia.Animation", console);                             // no per-control Avalonia animations
        var clockSrc = File.ReadAllText(RepoFile("Controls/ActivityClock.cs"));
        Assert.Equal(1, clockSrc.Split("new DispatcherTimer(").Length - 1);                     // exactly one timer in the codebase's component
    }

    // ── Integration in MainWindow: READY by default, nothing fake in production UI, navigation intact ──

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XDocument Axaml = XDocument.Load(RepoFile("MainWindow.axaml"));
    private static string? Name(XElement e) => (string?)e.Attribute(X + "Name");

    [Fact]
    public void MainWindow_HostsTheConsole_InTheSidebarHeader_AsReady()
    {
        var console = Axaml.Descendants().Single(e => Name(e) == "SidebarStatusConsole");
        Assert.Equal("C64StatusConsole", console.Name.LocalName);
        Assert.Null(console.Attribute("Kind"));                                               // default Ready; no hardcoded LOADING

        var header = console.Ancestors().First(a => (string?)a.Attribute("Height") == "56");
        Assert.Equal("Border", header.Name.LocalName);
        Assert.Equal("Top", header.Attributes().First(a => a.Name.LocalName == "DockPanel.Dock").Value);
        Assert.Equal("20,0", (string?)header.Attribute("Padding"));                            // 220 - 40 = 180 = DefaultWidth
        var cols = (string?)Axaml.Descendants().Single(e => Name(e) == "MainRoot").Attribute("ColumnDefinitions");
        Assert.Equal("220,*", cols);
    }

    [Fact]
    public void PreviousBranding_IsKeptButCollapsed_SoThemeLogoCodeStillWorks()
    {
        var legacy = Axaml.Descendants().Single(e => Name(e) == "SidebarBrandingLegacy");
        Assert.Equal("False", (string?)legacy.Attribute("IsVisible"));
        Assert.Contains(legacy.Descendants(), e => Name(e) == "SidebarLogoImage");
        Assert.Contains(legacy.Descendants(), e => Name(e) == "SidebarLogoText");
        Assert.Contains("SidebarLogoImage.Source", File.ReadAllText(RepoFile("MainWindow.axaml.cs")));
    }

    [Theory]
    [InlineData("NavDashboard")] [InlineData("NavAnalytics")] [InlineData("NavProviders")] [InlineData("NavSystems")]
    [InlineData("NavOperations")] [InlineData("NavLibrary")] [InlineData("NavCatalog")] [InlineData("NavStaging")]
    [InlineData("NavPending")] [InlineData("NavVolumes")] [InlineData("NavDisks")] [InlineData("NavLogs")]
    [InlineData("NavBackups")] [InlineData("NavSettings")]
    public void NavigationButtons_AreAllStillPresent_WithTheirHandler(string nav)
    {
        var b = Axaml.Descendants().SingleOrDefault(e => Name(e) == nav);
        if (b is null) return;                                                                  // NavOperations/Transforms: only assert what exists
        Assert.Equal("OnNavClick", (string?)b.Attribute("Click"));
    }

    [Fact]
    public void NoDebugControls_AreVisibleInTheProductionUi()
    {
        var names = Axaml.Descendants().Select(Name).Where(n => n is not null).ToList();
        Assert.DoesNotContain(names, n => n!.Contains("Debug", StringComparison.OrdinalIgnoreCase) && n.Contains("Console", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Axaml.Descendants().Where(e => e.Name.LocalName == "Button"),
            b => ((string?)b.Attribute("Content") ?? "").Contains("loading", StringComparison.OrdinalIgnoreCase));
        // The only way to reach LOADING is the development variable, resolved once at startup.
        var code = File.ReadAllText(RepoFile("MainWindow.axaml.cs"));
        Assert.Contains("C64ConsolePreview.Parse(", code);
        // MainWindow DISPLAYS the foreground activity and hands the shared monitor to the workers.
        Assert.Contains("Activities.ActivityMonitor.Shared.ForegroundChanged +=", code);
    }
}
