using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Systems;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// PLATFORMS-TOOLBAR-1 / FIX-1: single-row overflow toolbar. The planner is pure; the controller is driven with real
/// Avalonia controls given FIXED widths (no theme/platform needed, deterministic): "windows" are simulated by the
/// available width handed to <see cref="ToolbarOverflowController.Update"/>, exactly what the real SizeChanged hook does.
/// </summary>
public sealed class ToolbarOverflowTests
{
    private const double Spacing = 6;
    private const double ChevronW = 30;
    private const double SepW = 21;        // 1px border + 10px margins each side, as in the real bar

    // Mirrors the real bar: System hdr + 3 cmds | DAT hdr + 8 cmds | 2 cmds | 1 cmd.
    private static readonly (string Key, string Label, string? Header, bool Danger)[] Commands =
    {
        ("SysActCreatePlatform", "Create", "System", false),
        ("SysActEditPlatform",   "Edit",   "System", false),
        ("SysActDeletePlatform", "Delete", "System", true),
        ("SysActImportDat",      "Import",    "DAT", false),
        ("SysActPreviewGroupDat","Group",     "DAT", false),
        ("SysActConfigureDat",   "Configure", "DAT", false),
        ("SysActUpdateDat",      "Update",    "DAT", false),
        ("SysActVerifyAll",      "Verify",    "DAT", false),     // historical x:Name: the contextual Verify
        ("SysActVerifyDat",      "Verify All","DAT", false),     // historical x:Name: Verify All
        ("SysActDeleteDat",      "Delete",    "DAT", true),
        ("SysActDeleteGroup",    "Delete Group","DAT", true),
        ("SysActIngestFiles",    "Ingest Files", null, false),
        ("SysActVerifyArchive",  "Verify Archive", null, false),
        ("OpenDatDownloader",    "DAT Downloader", null, false),
    };

    private sealed class Bar
    {
        public StackPanel Items { get; } = new() { Orientation = Avalonia.Layout.Orientation.Horizontal };
        public Button Chevron { get; } = new() { Width = ChevronW, IsVisible = false };
        public ToolbarOverflowController Controller { get; }
        public Dictionary<string, Button> Buttons { get; } = new();
        public List<(string Key, object? Sender)> Clicks { get; } = new();

        public Bar()
        {
            // ContentControl stands in for the TextBlock label: text measurement needs a font manager (a platform service);
            // the controller identifies headers by the "toolbar-section" class, exactly as in the real AXAML.
            void Header(string text) => Items.Children.Add(new ContentControl { Content = text, Width = 50, Classes = { "toolbar-section" } });
            void Sep() => Items.Children.Add(new Border { Width = 1, Margin = new Avalonia.Thickness(10, 0) });

            Header("System");
            foreach (var c in Commands.Take(3)) Add(c);
            Sep();
            Header("DAT");
            foreach (var c in Commands.Skip(3).Take(8)) Add(c);
            Sep();
            foreach (var c in Commands.Skip(11).Take(2)) Add(c);
            Sep();
            Add(Commands[13]);

            Controller = new ToolbarOverflowController(Items, Chevron, Spacing);
        }

        private void Add((string Key, string Label, string? Header, bool Danger) c)
        {
            var b = new Button { Content = c.Label, Width = 40 + c.Label.Length * 6, Name = null };
            if (c.Danger) b.Classes.Add("danger");
            ToolTip.SetTip(b, "tip " + c.Label);
            Avalonia.Automation.AutomationProperties.SetName(b, "auto " + c.Key);
            var key = c.Key;
            b.Click += (s, _) => Clicks.Add((key, s));
            Buttons[key] = b;
            Items.Children.Add(b);
        }

        public double TotalWidth => Items.Children.OfType<Control>().Sum(Measure) + Spacing * (Items.Children.Count - 1);

        public static double Measure(Control c)
        {
            c.Measure(Avalonia.Size.Infinity);
            return c.DesiredSize.Width;
        }

        public double VisibleRowWidth()
        {
            var vis = Items.Children.OfType<Control>().Where(c => c.IsVisible).ToList();
            return vis.Sum(c => c.Width > 0 ? c.Width + c.Margin.Left + c.Margin.Right : 0) + Spacing * Math.Max(0, vis.Count - 1);
        }

        public IEnumerable<string> VisibleCommandKeys() =>
            Buttons.Where(kv => kv.Value.IsVisible).OrderBy(kv => Items.Children.IndexOf(kv.Value)).Select(kv => kv.Key);

        public IEnumerable<string> HiddenCommandKeys() =>
            Buttons.Where(kv => !kv.Value.IsVisible).OrderBy(kv => Items.Children.IndexOf(kv.Value)).Select(kv => kv.Key);

        public List<MenuItem> CommandMenuItems() =>
            Controller.BuildMenuItems().OfType<MenuItem>().Where(m => m.Tag is Button).ToList();

        public MenuItem MenuItemFor(string key) => CommandMenuItems().Single(m => ReferenceEquals(m.Tag, Buttons[key]));

        public static void Invoke(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    }

    private static double FullWidth() => new Bar().TotalWidth;

    // ── Planner (pure) ──────────────────────────────────────────────────────────

    private static List<ToolbarItemInfo> PlannerItems()
    {
        var bar = new Bar();
        return bar.Items.Children.OfType<Control>()
            .Select(c => new ToolbarItemInfo(ToolbarOverflowController.KindOf(c), Bar.Measure(c))).ToList();
    }

    [Fact]
    public void Planner_WideWindow_ShowsEverything_NoOverflow()
    {
        var items = PlannerItems();
        var plan = ToolbarOverflowPlanner.Plan(items, 5000, Spacing, ChevronW);
        Assert.All(plan.Visible, Assert.True);
        Assert.False(plan.HasOverflow);
    }

    [Fact]
    public void Planner_ExactFit_NeedsNoOverflow_OnePixelLess_Does()
    {
        var items = PlannerItems();
        var total = items.Sum(i => i.Width) + Spacing * (items.Count - 1);
        Assert.False(ToolbarOverflowPlanner.Plan(items, total, Spacing, ChevronW).HasOverflow);
        Assert.True(ToolbarOverflowPlanner.Plan(items, total - 1, Spacing, ChevronW).HasOverflow);
    }

    [Fact]
    public void Planner_EmptyBar_IsHarmless()
        => Assert.False(ToolbarOverflowPlanner.Plan(Array.Empty<ToolbarItemInfo>(), 100, Spacing, ChevronW).HasOverflow);

    [Fact]
    public void Planner_InvariantsHoldAtEveryWidth()
    {
        var items = PlannerItems();
        int lastCommandCount = -1;

        var limitW = Math.Ceiling(items.Sum(i => i.Width) + Spacing * (items.Count - 1)) + 5;
        for (double w = 0; w <= limitW; w += 1)
        {
            var plan = ToolbarOverflowPlanner.Plan(items, w, Spacing, ChevronW);
            var vis = plan.Visible;

            // Order preserved: visible commands are a PREFIX of all commands.
            var cmdIdx = Enumerable.Range(0, items.Count).Where(i => items[i].Kind == ToolbarItemKind.Command).ToList();
            var visCmd = cmdIdx.Select(i => vis[i]).ToList();
            Assert.True(visCmd.SkipWhile(v => v).All(v => !v), $"width {w}: visible commands are not a prefix");

            // No orphan headers / separators.
            for (int i = 0; i < items.Count; i++)
            {
                if (!vis[i]) continue;
                int next = i + 1; while (next < items.Count && !vis[next]) next++;
                int prev = i - 1; while (prev >= 0 && !vis[prev]) prev--;
                if (items[i].Kind == ToolbarItemKind.Header)
                    Assert.True(next < items.Count && items[next].Kind == ToolbarItemKind.Command, $"width {w}: orphan header at {i}");
                if (items[i].Kind == ToolbarItemKind.Separator)
                {
                    Assert.True(prev >= 0 && items[prev].Kind == ToolbarItemKind.Command, $"width {w}: separator at {i} has no command before");
                    Assert.True(next < items.Count && items[next].Kind != ToolbarItemKind.Separator, $"width {w}: separator at {i} trails/doubles");
                }
            }

            // Fits the row (reserving the chevron when it is shown).
            var shown = Enumerable.Range(0, items.Count).Where(i => vis[i]).ToList();
            if (shown.Count > 0)
            {
                var used = shown.Sum(i => items[i].Width) + Spacing * (shown.Count - 1);
                var limit = plan.HasOverflow ? w - (ChevronW + Spacing) : w;
                Assert.True(used <= limit + 0.01, $"width {w}: row {used} exceeds {limit}");
            }

            // Overflow button shown iff at least one command is hidden.
            Assert.Equal(visCmd.Any(v => !v), plan.HasOverflow);

            // Growing the window never hides a command that was visible at a smaller width.
            var count = visCmd.Count(v => v);
            Assert.True(count >= lastCommandCount, $"width {w}: command count shrank from {lastCommandCount} to {count}");
            lastCommandCount = count;
        }
        Assert.Equal(items.Count(i => i.Kind == ToolbarItemKind.Command), lastCommandCount);   // full width shows everything
    }

    [Fact]
    public void Planner_NarrowerThanTheChevron_StillReportsOverflow_AndHidesAllCommands()
    {
        var plan = ToolbarOverflowPlanner.Plan(PlannerItems(), 10, Spacing, ChevronW);
        Assert.True(plan.HasOverflow);
        Assert.All(plan.Visible, Assert.False);
    }

    // ── Controller: wide / medium / narrow ──────────────────────────────────────

    [Fact]
    public void Wide_AllCommandsVisible_NoChevron_EmptyMenu()
    {
        var bar = new Bar();
        bar.Controller.Update(5000);
        Assert.All(bar.Items.Children.OfType<Control>(), c => Assert.True(c.IsVisible));
        Assert.False(bar.Chevron.IsVisible);
        Assert.Empty(bar.Controller.BuildMenuItems());
    }

    [Fact]
    public void Medium_SomeCommandsOverflow_ChevronShown_MenuListsExactlyTheHiddenOnes_InOrder()
    {
        var bar = new Bar();
        bar.Controller.Update(FullWidth() - 120);

        Assert.True(bar.Chevron.IsVisible);
        var hidden = bar.HiddenCommandKeys().ToList();
        Assert.NotEmpty(hidden);
        Assert.NotEmpty(bar.VisibleCommandKeys());
        Assert.Equal(hidden, bar.CommandMenuItems().Select(m => ((Button)m.Tag!).Name ?? Key(bar, (Button)m.Tag!)));
        // Every command is either on the bar or in the menu, never both, never neither.
        Assert.Equal(Commands.Length, bar.VisibleCommandKeys().Count() + hidden.Count);
    }

    private static string Key(Bar bar, Button b) => bar.Buttons.Single(kv => ReferenceEquals(kv.Value, b)).Key;

    [Fact]
    public void Narrow_NoCommandIsLost_EveryOneIsInTheMenu()
    {
        var bar = new Bar();
        bar.Controller.Update(40);            // barely wider than the chevron

        Assert.True(bar.Chevron.IsVisible);
        Assert.Empty(bar.VisibleCommandKeys());
        var inMenu = bar.CommandMenuItems().Select(m => Key(bar, (Button)m.Tag!)).ToList();
        Assert.Equal(Commands.Select(c => c.Key), inMenu);              // all 14, original order
    }

    [Fact]
    public void Resizing_BothDirections_RestoresAndHidesCommandsAutomatically()
    {
        var bar = new Bar();
        var full = FullWidth();
        int Visible() => bar.VisibleCommandKeys().Count();

        bar.Controller.Update(full + 50);           Assert.Equal(Commands.Length, Visible());
        bar.Controller.Update(full * 0.6);          var mid = Visible();   Assert.InRange(mid, 1, Commands.Length - 1);
        bar.Controller.Update(full * 0.3);          var low = Visible();   Assert.True(low < mid);
        bar.Controller.Update(40);                  Assert.Equal(0, Visible());
        bar.Controller.Update(full * 0.3);          Assert.Equal(low, Visible());       // grows back: same result as before
        bar.Controller.Update(full * 0.6);          Assert.Equal(mid, Visible());
        bar.Controller.Update(full + 50);           Assert.Equal(Commands.Length, Visible());
        Assert.False(bar.Chevron.IsVisible);
        Assert.All(bar.Items.Children.OfType<Control>(), c => Assert.True(c.IsVisible));
    }

    [Fact]
    public void AtEveryWidth_ThereIsExactlyOneRow_TheVisibleItemsFitAndNothingIsLost()
    {
        var bar = new Bar();
        var full = FullWidth();
        for (double w = 45; w <= full + 40; w += 7)
        {
            bar.Controller.Update(w);
            var limit = bar.Chevron.IsVisible ? w - (ChevronW + Spacing) : w;
            Assert.True(bar.VisibleRowWidth() <= limit + 0.01, $"width {w}: {bar.VisibleRowWidth()} > {limit}");
            // reachable = on the bar OR in the menu
            Assert.Equal(Commands.Length, bar.VisibleCommandKeys().Count() + bar.CommandMenuItems().Count);
        }
    }

    [Fact]
    public void Update_IsIdempotent_AndIgnoresZeroWidth()
    {
        var bar = new Bar();
        bar.Controller.Update(FullWidth() - 100);
        var first = bar.Controller.Visible.ToList();
        bar.Controller.Update(FullWidth() - 100);
        Assert.Equal(first, bar.Controller.Visible);
        bar.Controller.Update(0);               // hidden view (Bounds 0): must not collapse the bar
        Assert.Equal(first, bar.Controller.Visible);
    }

    // ── No orphan section labels / separators (real controls) ───────────────────

    [Fact]
    public void OnTheBar_NoOrphanHeadersOrSeparators_AtAnyWidth()
    {
        var bar = new Bar();
        var full = FullWidth();
        for (double w = 45; w <= full + 40; w += 3)
        {
            bar.Controller.Update(w);
            var vis = bar.Items.Children.OfType<Control>().Where(c => c.IsVisible).ToList();
            for (int i = 0; i < vis.Count; i++)
            {
                var kind = ToolbarOverflowController.KindOf(vis[i]);
                if (kind == ToolbarItemKind.Header)
                    Assert.True(i + 1 < vis.Count && vis[i + 1] is Button, $"width {w}: header without a command after it");
                if (kind == ToolbarItemKind.Separator)
                    Assert.True(i > 0 && vis[i - 1] is Button && i + 1 < vis.Count, $"width {w}: dangling separator");
            }
            Assert.True(vis.Count == 0 || vis[^1] is Button, $"width {w}: bar ends on a non-command");
        }
    }

    [Fact]
    public void InTheMenu_HeadingsAndSeparators_AreOnlyBetweenCommands_AndKeepTheLogicalGroups()
    {
        var bar = new Bar();
        bar.Controller.Update(40);
        var menu = bar.Controller.BuildMenuItems();

        Assert.IsType<MenuItem>(menu[0]);                       // starts with the System heading, not a separator
        Assert.NotNull(menu[0].Tag is null ? "heading" : null); // headings carry no command
        Assert.IsNotType<Separator>(menu[^1]);
        for (int i = 0; i < menu.Count; i++)
        {
            if (menu[i] is Separator) { Assert.True(i > 0 && i < menu.Count - 1); Assert.IsNotType<Separator>(menu[i + 1]); }
            if (menu[i] is MenuItem { Tag: null } heading)
            {
                Assert.False(heading.IsHitTestVisible);
                Assert.False(heading.Focusable);
                Assert.True(i + 1 < menu.Count && menu[i + 1] is MenuItem { Tag: Button });   // heading always followed by a command
            }
        }

        var headings = menu.OfType<MenuItem>().Where(m => m.Tag is null).Select(m => ((TextBlock)m.Header!).Text).ToList();
        Assert.Equal(new[] { "System", "DAT" }, headings);
        // 2 separators: System|DAT and DAT|(Ingest/Verify Archive), plus the one before DAT Downloader = 3
        Assert.Equal(3, menu.OfType<Separator>().Count());
    }

    [Fact]
    public void InTheMenu_PartialSection_KeepsItsHeading()
    {
        var bar = new Bar();
        // Hide only the tail: part of the DAT section ends up in the menu, so it must carry the DAT heading.
        bar.Controller.Update(FullWidth() - 200);
        var menu = bar.Controller.BuildMenuItems();
        var firstHidden = bar.HiddenCommandKeys().First();
        if (Commands.First(c => c.Key == firstHidden).Header == "DAT")
            Assert.Contains(menu.OfType<MenuItem>(), m => m.Tag is null && ((TextBlock)m.Header!).Text == "DAT");
    }

    // ── Command identity and routing ────────────────────────────────────────────

    [Fact]
    public void EveryHiddenCommand_RoutesToItsOwnOriginalButton_ExactlyOnce()
    {
        var bar = new Bar();
        bar.Controller.Update(40);              // everything in the menu

        foreach (var (key, _, _, _) in Commands)
        {
            bar.Clicks.Clear();
            Bar.Invoke(bar.MenuItemFor(key));
            var click = Assert.Single(bar.Clicks);
            Assert.Equal(key, click.Key);
            Assert.Same(bar.Buttons[key], click.Sender);        // the original button is the sender
        }
    }

    [Fact]
    public void Verify_And_VerifyAll_AreNeverSwapped_WhenInvokedFromTheMenu()
    {
        var bar = new Bar();
        bar.Controller.Update(40);

        // Label "Verify" belongs to the button historically named SysActVerifyAll ...
        var verify = bar.CommandMenuItems().Single(m => (string?)m.Header == "Verify");
        Assert.Same(bar.Buttons["SysActVerifyAll"], verify.Tag);
        bar.Clicks.Clear(); Bar.Invoke(verify);
        Assert.Equal("SysActVerifyAll", Assert.Single(bar.Clicks).Key);

        // ... and "Verify All" to SysActVerifyDat.
        var verifyAll = bar.CommandMenuItems().Single(m => (string?)m.Header == "Verify All");
        Assert.Same(bar.Buttons["SysActVerifyDat"], verifyAll.Tag);
        bar.Clicks.Clear(); Bar.Invoke(verifyAll);
        Assert.Equal("SysActVerifyDat", Assert.Single(bar.Clicks).Key);
    }

    [Fact]
    public void GroupCommands_RouteToTheirOwnButtons_FromTheMenu()
    {
        var bar = new Bar();
        bar.Controller.Update(40);
        foreach (var key in new[] { "SysActPreviewGroupDat", "SysActDeleteGroup" })
        {
            bar.Clicks.Clear();
            Bar.Invoke(bar.MenuItemFor(key));
            Assert.Equal(key, Assert.Single(bar.Clicks).Key);
        }
    }

    [Fact]
    public void MenuItems_CarryNoControlNames_AndPointToTheOriginals_NoDuplicateImplementation()
    {
        var bar = new Bar();
        bar.Controller.Update(40);
        var items = bar.CommandMenuItems();
        Assert.All(items, m => Assert.True(string.IsNullOrEmpty(m.Name)));
        Assert.Equal(items.Count, items.Select(m => m.Tag).Distinct().Count());
    }

    [Fact]
    public void MenuItem_KeepsTooltip_AndAccessibleName_OfTheOriginal()
    {
        var bar = new Bar();
        bar.Controller.Update(40);
        var m = bar.MenuItemFor("SysActDeleteDat");
        Assert.Equal("tip Delete", ToolTip.GetTip(m));
        Assert.Equal("auto SysActDeleteDat", Avalonia.Automation.AutomationProperties.GetName(m));
    }

    // ── Disabled state ──────────────────────────────────────────────────────────

    [Fact]
    public void DisabledOriginal_IsDisabledInTheMenu_AndEnabledAgainWhenReEnabled()
    {
        var bar = new Bar();
        bar.Controller.Update(40);

        bar.Buttons["SysActUpdateDat"].IsEnabled = false;
        bar.Buttons["SysActDeleteGroup"].IsEnabled = false;
        Assert.False(bar.MenuItemFor("SysActUpdateDat").IsEnabled);
        Assert.False(bar.MenuItemFor("SysActDeleteGroup").IsEnabled);
        Assert.True(bar.MenuItemFor("SysActImportDat").IsEnabled);          // untouched ones stay enabled

        bar.Buttons["SysActUpdateDat"].IsEnabled = true;                    // menu is rebuilt on each open
        Assert.True(bar.MenuItemFor("SysActUpdateDat").IsEnabled);
    }

    [Fact]
    public void DisabledState_IsPreservedOnTheBarButtons_WhileTheyAreHiddenAndShown()
    {
        var bar = new Bar();
        bar.Buttons["SysActUpdateDat"].IsEnabled = false;
        bar.Controller.Update(40);
        bar.Controller.Update(5000);
        Assert.False(bar.Buttons["SysActUpdateDat"].IsEnabled);              // overflow never touches IsEnabled
        Assert.True(bar.Buttons["SysActImportDat"].IsEnabled);
    }

    // ── Danger semantics ────────────────────────────────────────────────────────

    [Fact]
    public void DestructiveCommands_KeepDangerStyling_InTheMenu_OthersStayNeutral()
    {
        var bar = new Bar();
        bar.Controller.Update(40);

        foreach (var (key, _, _, danger) in Commands)
        {
            var fg = bar.MenuItemFor(key).Foreground as ISolidColorBrush;
            var isRed = fg is not null && fg.Color == Color.Parse("#EF5350");
            Assert.Equal(danger, isRed);
        }
        Assert.Equal(new[] { "SysActDeletePlatform", "SysActDeleteDat", "SysActDeleteGroup" },
                     Commands.Where(c => c.Danger).Select(c => c.Key));
    }

    [Fact]
    public void BarButtons_KeepTheirDangerClass_AfterOverflowCycles()
    {
        var bar = new Bar();
        bar.Controller.Update(40);
        bar.Controller.Update(5000);
        foreach (var (key, _, _, danger) in Commands)
            Assert.Equal(danger, bar.Buttons[key].Classes.Contains("danger"));
    }

    [Fact]
    public void InvalidateMeasurements_ReplansWithFreshWidths_AtTheLastKnownWidth()
    {
        var bar = new Bar();
        var w = FullWidth() + 5;
        bar.Controller.Update(w);
        Assert.False(bar.Chevron.IsVisible);

        foreach (var b in bar.Buttons.Values) b.Width += 40;         // e.g. a larger font after a theme change
        bar.Controller.Update(w);
        Assert.False(bar.Chevron.IsVisible);                         // stale cache: not noticed yet
        bar.Controller.InvalidateMeasurements();
        Assert.True(bar.Chevron.IsVisible);                          // now re-measured and re-planned
    }

    // ── Wiring ──────────────────────────────────────────────────────────────────

    // ── FIX-2: native flyout, populated BEFORE it is shown ──────────────────────

    [Fact]
    public void Flyout_IsANativeMenuFlyout_AlreadyPopulatedBeforeItIsShown()
    {
        // Root cause of FIX-2: items added in MenuFlyout.Opening were not realized (the popup opened as an empty
        // 10x18 presenter, a hairline under the chevron). The flyout must contain every hidden command at creation.
        var bar = new Bar();
        bar.Controller.Update(40);

        var flyout = bar.Controller.CreateFlyout();

        Assert.IsAssignableFrom<Avalonia.Controls.Primitives.PopupFlyoutBase>(flyout);   // hosted by Avalonia's popup layer
        Assert.Equal(bar.Controller.BuildMenuItems().Count, flyout.Items.Count);
        Assert.Equal(Commands.Length, flyout.Items.OfType<MenuItem>().Count(m => m.Tag is Button));
        Assert.Equal(Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight, flyout.Placement);   // under the chevron, grows leftwards
    }

    [Fact]
    public void Chevron_HasNoFlyoutProperty_AndEachOpenGetsAFreshFlyout()
    {
        var bar = new Bar();
        bar.Controller.Update(40);

        Assert.Null(bar.Chevron.Flyout);                                  // the controller opens its own flyout on Click
        var first  = bar.Controller.CreateFlyout();
        var second = bar.Controller.CreateFlyout();
        Assert.NotSame(first, second);                                    // close + reopen never reuses a stale presenter
        Assert.Equal(first.Items.Count, second.Items.Count);
    }

    [Fact]
    public void ReopenedFlyout_ReflectsCurrentEnabledState()
    {
        var bar = new Bar();
        bar.Controller.Update(40);

        bool Enabled(MenuFlyout f) => f.Items.OfType<MenuItem>().Single(m => ReferenceEquals(m.Tag, bar.Buttons["SysActUpdateDat"])).IsEnabled;

        Assert.True(Enabled(bar.Controller.CreateFlyout()));
        bar.Buttons["SysActUpdateDat"].IsEnabled = false;
        Assert.False(Enabled(bar.Controller.CreateFlyout()));
        bar.Buttons["SysActUpdateDat"].IsEnabled = true;
        Assert.True(Enabled(bar.Controller.CreateFlyout()));
    }

    [Fact]
    public void FlyoutItems_RunTheOriginalOperation_ExactlyOnce_EvenThoughTheOriginalButtonIsHidden()
    {
        var bar = new Bar();
        bar.Controller.Update(40);
        Assert.False(bar.Buttons["SysActVerifyDat"].IsVisible);           // hidden with IsVisible=false

        var flyout = bar.Controller.CreateFlyout();
        var item = flyout.Items.OfType<MenuItem>().Single(m => ReferenceEquals(m.Tag, bar.Buttons["SysActVerifyDat"]));
        Bar.Invoke(item);

        var click = Assert.Single(bar.Clicks);
        Assert.Equal("SysActVerifyDat", click.Key);
        Assert.Same(bar.Buttons["SysActVerifyDat"], click.Sender);
    }

    [Fact]
    public void Flyout_KeepsHeadingsSeparatorsAndOrder_AndDisabledItemsDisabled()
    {
        var bar = new Bar();
        bar.Controller.Update(40);
        bar.Buttons["SysActDeleteGroup"].IsEnabled = false;

        var flyout = bar.Controller.CreateFlyout();
        Assert.Equal(bar.Controller.BuildMenuItems().Select(c => c.GetType()), flyout.Items.Cast<Control>().Select(c => c.GetType()));
        Assert.False(flyout.Items.OfType<MenuItem>().Single(m => ReferenceEquals(m.Tag, bar.Buttons["SysActDeleteGroup"])).IsEnabled);
    }

    [Fact]
    public void Controller_OpensThroughClick_NotThroughAnOpeningHandler_Source()
    {
        var src = File.ReadAllText(RepoFile("Systems/ToolbarOverflowController.cs"));
        Assert.Contains("_overflowButton.Click += (_, _) => ShowMenu();", src);
        Assert.Contains("_openFlyout.ShowAt(_overflowButton);", src);
        Assert.DoesNotContain(".Opening +=", src);                         // the pattern that produced the empty popup
        Assert.DoesNotContain("ZIndex", src);                              // no z-order hack: the popup layer is native
    }

    [Fact]
    public void ChevronAncestors_DoNotClip_AndNoZIndexHackIsUsed()
    {
        var doc = System.Xml.Linq.XDocument.Load(RepoFile("MainWindow.axaml"));
        var x = (System.Xml.Linq.XNamespace)"http://schemas.microsoft.com/winfx/2006/xaml";
        var chevron = doc.Descendants().First(e => (string?)e.Attribute(x + "Name") == "SysActOverflow");

        foreach (var anc in chevron.Ancestors().TakeWhile(a => (string?)a.Attribute(x + "Name") != "ViewSystems"))
            Assert.NotEqual("True", (string?)anc.Attribute("ClipToBounds"));          // the clipped StackPanel is a SIBLING, not an ancestor
        Assert.Null(chevron.Attribute("ZIndex"));
        Assert.Null(chevron.Parent!.Attribute("ZIndex"));
    }

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }
}
