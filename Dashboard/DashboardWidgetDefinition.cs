namespace Arkadia.Dashboard;

/// <summary>
/// Describes a single dashboard widget: its identity, display group, sort priority,
/// and whether it is shown by default.
/// </summary>
public sealed class DashboardWidgetDefinition
{
    /// <summary>Stable widget identifier. See <see cref="DashboardWidgetId"/>.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Display priority — lower numbers appear first.
    /// The layout engine sorts visible widgets by this value.
    /// </summary>
    public required int Priority { get; init; }

    /// <summary>Structural group controlling layout-mode eligibility.</summary>
    public required DashboardWidgetGroup Group { get; init; }

    /// <summary>
    /// True if the widget is shown out-of-the-box without user configuration.
    /// Disabled-by-default widgets are excluded from all layout modes until the user enables them.
    /// </summary>
    public required bool EnabledByDefault { get; init; }
}
