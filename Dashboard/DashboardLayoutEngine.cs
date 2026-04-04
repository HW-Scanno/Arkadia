using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Dashboard;

/// <summary>
/// Resolves which widgets should be visible for a given content width.
/// <para>
/// Layout mode thresholds (applied to the content area width, i.e. after the sidebar):
/// <list type="bullet">
///   <item>Compact  — &lt; 700 px</item>
///   <item>Standard — 700–1099 px</item>
///   <item>Wide     — ≥ 1100 px</item>
/// </list>
/// </para>
/// </summary>
public sealed class DashboardLayoutEngine
{
    private readonly IReadOnlyList<DashboardWidgetDefinition> _registry;

    /// <param name="registry">
    /// Widget definitions to use. Defaults to <see cref="DashboardWidgetRegistry.All"/>.
    /// Injecting a custom registry makes the engine fully unit-testable.
    /// </param>
    public DashboardLayoutEngine(IReadOnlyList<DashboardWidgetDefinition>? registry = null)
    {
        _registry = registry ?? DashboardWidgetRegistry.All;
    }

    /// <summary>Resolves the layout mode for the given content-area width in device-independent pixels.</summary>
    public static DashboardLayoutMode ResolveMode(double contentWidth) => contentWidth switch
    {
        < 700  => DashboardLayoutMode.Compact,
        < 1100 => DashboardLayoutMode.Standard,
        _      => DashboardLayoutMode.Wide,
    };

    /// <summary>
    /// Returns the ordered list of widgets that should be visible for the given layout mode.
    /// <para>
    /// Selection rules:
    /// <list type="number">
    ///   <item>Only enabled-by-default (or user-enabled) widgets are considered.</item>
    ///   <item>Compact mode excludes Optional-group widgets entirely.</item>
    ///   <item>Standard and Wide include all enabled widgets (Optional included when enabled).</item>
    ///   <item>Results are sorted by priority — lower priority number appears first.</item>
    /// </list>
    /// Core widgets always appear before optional widgets because their priority numbers are lower.
    /// </para>
    /// </summary>
    public IReadOnlyList<DashboardWidgetDefinition> ResolveWidgets(DashboardLayoutMode mode)
    {
        return _registry
            .Where(w => w.EnabledByDefault)
            .Where(w => mode == DashboardLayoutMode.Compact
                ? w.Group != DashboardWidgetGroup.Optional
                : true)
            .OrderBy(w => w.Priority)
            .ToList();
    }
}
