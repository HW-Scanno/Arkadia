namespace Arkadia.Dashboard;

/// <summary>
/// Structural group of a dashboard widget.
/// The layout engine uses the group to decide widget eligibility for a given layout mode.
/// </summary>
public enum DashboardWidgetGroup
{
    /// <summary>Compact summary cards shown at the top of the dashboard.</summary>
    Summary,

    /// <summary>Full-width content panels shown below the summary cards.</summary>
    Main,

    /// <summary>Optional panels shown only in Standard and Wide modes when enabled.</summary>
    Optional,
}
