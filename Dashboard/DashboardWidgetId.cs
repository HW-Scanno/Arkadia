namespace Arkadia.Dashboard;

/// <summary>
/// Stable string identifiers for all v1 dashboard widgets.
/// Using string constants (rather than an enum) keeps the registry extensible without
/// casting and allows future user-defined widgets to use the same identity model.
/// </summary>
public static class DashboardWidgetId
{
    // ── Core Summary ─────────────────────────────────────────────────────────
    public const string LibraryCoverage      = "LibraryCoverage";
    public const string Volumes              = "Volumes";
    public const string Systems              = "Systems";
    public const string Storage              = "Storage";
    public const string RecentActivitySummary = "RecentActivitySummary";

    // ── Core Main ─────────────────────────────────────────────────────────────
    public const string RecentOperations     = "RecentOperations";
    public const string AttentionRequired    = "AttentionRequired";

    // ── Optional ─────────────────────────────────────────────────────────────
    public const string ArchiveFormats       = "ArchiveFormats";
    public const string DiskHealth           = "DiskHealth";
    public const string PendingWork          = "PendingWork";
    public const string RecentVolumes        = "RecentVolumes";
}
