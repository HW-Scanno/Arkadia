namespace Arkadia.Dashboard;

/// <summary>The three layout modes resolved from available content width.</summary>
public enum DashboardLayoutMode
{
    /// <summary>Narrow content area (&lt; 700 px). Core widgets only; no optional widgets.</summary>
    Compact,

    /// <summary>Normal content area (700–1099 px). Core + enabled optional widgets.</summary>
    Standard,

    /// <summary>Wide content area (≥ 1100 px). Core + all enabled optional widgets; room for future expansion.</summary>
    Wide,
}
