using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Dashboard;

/// <summary>
/// The v1 widget registry — the single source of truth for all dashboard widgets,
/// their priorities, groups, and default-enabled state.
/// </summary>
public static class DashboardWidgetRegistry
{
    /// <summary>All registered widgets in definition order.</summary>
    public static readonly IReadOnlyList<DashboardWidgetDefinition> All =
    [
        // ── Core Summary ─────────────────────────────────────────────────────
        new() { Id = DashboardWidgetId.LibraryCoverage,       Priority = 10,  Group = DashboardWidgetGroup.Summary,  EnabledByDefault = true  },
        new() { Id = DashboardWidgetId.Volumes,               Priority = 20,  Group = DashboardWidgetGroup.Summary,  EnabledByDefault = true  },
        new() { Id = DashboardWidgetId.Systems,               Priority = 25,  Group = DashboardWidgetGroup.Summary,  EnabledByDefault = true  },
        new() { Id = DashboardWidgetId.Storage,               Priority = 30,  Group = DashboardWidgetGroup.Summary,  EnabledByDefault = true  },
        new() { Id = DashboardWidgetId.RecentActivitySummary, Priority = 40,  Group = DashboardWidgetGroup.Summary,  EnabledByDefault = true  },

        // ── Core Main ─────────────────────────────────────────────────────────
        new() { Id = DashboardWidgetId.RecentOperations,      Priority = 50,  Group = DashboardWidgetGroup.Main,     EnabledByDefault = true  },
        new() { Id = DashboardWidgetId.AttentionRequired,     Priority = 60,  Group = DashboardWidgetGroup.Main,     EnabledByDefault = true  },

        // ── Optional ─────────────────────────────────────────────────────────
        new() { Id = DashboardWidgetId.ArchiveFormats,        Priority = 100, Group = DashboardWidgetGroup.Optional, EnabledByDefault = true  },
        new() { Id = DashboardWidgetId.DiskHealth,            Priority = 110, Group = DashboardWidgetGroup.Optional, EnabledByDefault = false },
        new() { Id = DashboardWidgetId.PendingWork,           Priority = 120, Group = DashboardWidgetGroup.Optional, EnabledByDefault = false },
        new() { Id = DashboardWidgetId.RecentVolumes,         Priority = 130, Group = DashboardWidgetGroup.Optional, EnabledByDefault = false },
    ];

    /// <summary>Widgets that are enabled by default, in priority order.</summary>
    public static IReadOnlyList<DashboardWidgetDefinition> Defaults =>
        All.Where(w => w.EnabledByDefault).OrderBy(w => w.Priority).ToList();
}
