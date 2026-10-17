using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia.Systems;

/// <summary>
/// Keeps an existing horizontal toolbar on ONE row. It measures the real children and the real available width
/// (no hardcoded window widths), asks <see cref="ToolbarOverflowPlanner"/> which items fit, hides the rest, and
/// shows a chevron button whose menu lists every hidden command.
///
/// <para>The toolbar's own buttons stay the single implementation of each command: a menu entry does not copy a
/// handler, it raises <c>Button.Click</c> on the ORIGINAL button, so the original (XAML-wired) handler runs with the
/// original button as sender — same operation, same confirmations, same selection requirements. Enabled state,
/// danger styling, tooltip and accessible name are read from the original button each time the menu opens, so
/// they can never drift. No control names are duplicated.</para>
///
/// <para>Item kinds: <see cref="Button"/> = command; any element carrying the <c>toolbar-section</c> style class (the
/// "System"/"DAT" <see cref="TextBlock"/> labels) = section header; anything else (the 1px <see cref="Border"/>) =
/// separator. A section header applies to the commands after it until the next separator.</para>
/// </summary>
public sealed class ToolbarOverflowController
{
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#EF5350"));

    private readonly Panel  _items;
    private readonly Button _overflowButton;
    private readonly double _spacing;
    private readonly Dictionary<Control, double> _widths = new();
    private IReadOnlyList<bool> _visible = Array.Empty<bool>();
    private bool _updating;
    private double _lastWidth;

    private MenuFlyout? _openFlyout;

    public ToolbarOverflowController(Panel itemsPanel, Button overflowButton, double spacing)
    {
        _items          = itemsPanel     ?? throw new ArgumentNullException(nameof(itemsPanel));
        _overflowButton = overflowButton ?? throw new ArgumentNullException(nameof(overflowButton));
        _spacing        = spacing;

        // The chevron has NO Button.Flyout of its own: each click builds a brand-new, fully populated MenuFlyout and
        // shows it (see CreateFlyout for why it must be populated BEFORE it is shown).
        _overflowButton.Click += (_, _) => ShowMenu();
    }

    /// <summary>
    /// A new native <see cref="MenuFlyout"/> already containing the current menu content. It MUST be populated before
    /// <c>ShowAt</c>: Avalonia creates the flyout presenter when the flyout is first shown, and items added later
    /// (e.g. in the <c>Opening</c> event, as the first version did) are not realized — the popup opened (a native
    /// <c>PopupRoot</c>, correctly above all window content and right under the chevron) but contained an empty
    /// 10x18 px presenter, which looked like a hairline "behind" the content. A fresh flyout per open also keeps
    /// enabled states current (they are read from the original buttons at that moment). Right-aligned to the chevron
    /// so the menu grows leftwards and stays inside the window.
    /// </summary>
    public MenuFlyout CreateFlyout()
    {
        var flyout = new MenuFlyout { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight };
        foreach (var item in BuildMenuItems()) flyout.Items.Add(item);
        return flyout;
    }

    private void ShowMenu()
    {
        _openFlyout?.Hide();
        _openFlyout = CreateFlyout();
        _openFlyout.ShowAt(_overflowButton);
    }

    /// <summary>Hooks the layout of <paramref name="host"/> (the row's container): re-plan whenever its width changes.</summary>
    public void Attach(Control host)
    {
        host.SizeChanged += (_, e) => Update(e.NewSize.Width);
        if (host.Bounds.Width > 0) Update(host.Bounds.Width);
    }

    /// <summary>Last applied plan (per child, in order).</summary>
    public IReadOnlyList<bool> Visible => _visible;

    /// <summary>Drops the cached item widths and re-plans at the last known width (e.g. after a font/theme change).</summary>
    public void InvalidateMeasurements()
    {
        _widths.Clear();
        if (_lastWidth > 0) Update(_lastWidth);
    }

    public const string SectionClass = "toolbar-section";

    public static ToolbarItemKind KindOf(Control c) =>
        c is Button                      ? ToolbarItemKind.Command :
        c.Classes.Contains(SectionClass) ? ToolbarItemKind.Header  :
                                           ToolbarItemKind.Separator;

    private static string? HeaderText(Control c) => c switch
    {
        TextBlock t       => t.Text,
        ContentControl cc => cc.Content?.ToString(),
        _                 => null,
    };

    /// <summary>Plans and applies visibility for a row of <paramref name="availableWidth"/>. Idempotent.</summary>
    public void Update(double availableWidth)
    {
        if (_updating || availableWidth <= 0) return;
        _lastWidth = availableWidth;
        _updating = true;
        try
        {
            var children = _items.Children.OfType<Control>().ToList();

            // Natural widths: cached; anything not yet known is measured once, temporarily visible.
            foreach (var c in children.Append(_overflowButton))
            {
                if (_widths.ContainsKey(c)) continue;
                var wasVisible = c.IsVisible;
                c.IsVisible = true;
                c.Measure(Size.Infinity);
                _widths[c] = c.DesiredSize.Width;
                c.IsVisible = wasVisible;
            }

            var infos = children.Select(c => new ToolbarItemInfo(KindOf(c), _widths[c])).ToList();
            var plan  = ToolbarOverflowPlanner.Plan(infos, availableWidth, _spacing, _widths[_overflowButton]);

            for (int i = 0; i < children.Count; i++)
                if (children[i].IsVisible != plan.Visible[i]) children[i].IsVisible = plan.Visible[i];
            if (_overflowButton.IsVisible != plan.HasOverflow) _overflowButton.IsVisible = plan.HasOverflow;

            _visible = plan.Visible;
        }
        finally { _updating = false; }
    }

    /// <summary>
    /// Menu content for the hidden commands, in toolbar order, grouped like the bar: a <see cref="Separator"/>
    /// between groups and a non-interactive section heading ("System"/"DAT") before the first hidden command of a
    /// section. Rebuilt on every open, so enabled state always reflects the original buttons.
    /// </summary>
    public IReadOnlyList<Control> BuildMenuItems()
    {
        var result = new List<Control>();
        int group = 0, lastGroup = -1;
        string? header = null, lastHeaderShown = null;

        foreach (var c in _items.Children.OfType<Control>())
        {
            switch (KindOf(c))
            {
                case ToolbarItemKind.Separator: group++; header = null; continue;
                case ToolbarItemKind.Header:    header = HeaderText(c); continue;
            }

            var button = (Button)c;
            if (button.IsVisible) continue;   // still on the bar

            if (group != lastGroup)
            {
                if (lastGroup != -1) result.Add(new Separator());
                lastGroup = group;
                lastHeaderShown = null;
            }
            if (header is not null && header != lastHeaderShown)
            {
                result.Add(MakeHeading(header));
                lastHeaderShown = header;
            }
            result.Add(MakeMenuItem(button));
        }
        return result;
    }

    private static Control MakeHeading(string text) => new MenuItem
    {
        Header = new TextBlock
        {
            Text = text, FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.5,
            Foreground = new SolidColorBrush(Color.Parse("#888899")),
        },
        IsHitTestVisible = false,
        Focusable        = false,
    };

    private static MenuItem MakeMenuItem(Button source)
    {
        var item = new MenuItem
        {
            Header    = source.Content?.ToString() ?? "",
            IsEnabled = source.IsEnabled,
            Tag       = source,
        };
        if (source.Classes.Contains("danger")) item.Foreground = DangerBrush;

        var tip = ToolTip.GetTip(source);
        if (tip is not null) ToolTip.SetTip(item, tip);
        var automationName = AutomationProperties.GetName(source);
        if (!string.IsNullOrEmpty(automationName)) AutomationProperties.SetName(item, automationName);

        // The one and only implementation of the command is the original button's Click handler.
        item.Click += (_, _) => source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, source));
        return item;
    }
}
