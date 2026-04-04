using System.Collections.Generic;
using System.Linq;
using Arkadia.Dashboard;
using Xunit;

namespace Arkadia.Tests.Dashboard;

public sealed class DashboardLayoutEngineTests
{
    // ── Registry ──────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_Contains_Eleven_Widgets()
    {
        Assert.Equal(11, DashboardWidgetRegistry.All.Count);
    }

    [Fact]
    public void Registry_Contains_All_DefinedIds()
    {
        var ids = DashboardWidgetRegistry.All.Select(w => w.Id).ToHashSet();

        Assert.Contains(DashboardWidgetId.LibraryCoverage,       ids);
        Assert.Contains(DashboardWidgetId.Volumes,               ids);
        Assert.Contains(DashboardWidgetId.Systems,               ids);
        Assert.Contains(DashboardWidgetId.Storage,               ids);
        Assert.Contains(DashboardWidgetId.RecentActivitySummary, ids);
        Assert.Contains(DashboardWidgetId.RecentOperations,      ids);
        Assert.Contains(DashboardWidgetId.AttentionRequired,     ids);
        Assert.Contains(DashboardWidgetId.ArchiveFormats,        ids);
        Assert.Contains(DashboardWidgetId.DiskHealth,            ids);
        Assert.Contains(DashboardWidgetId.PendingWork,           ids);
        Assert.Contains(DashboardWidgetId.RecentVolumes,         ids);
    }

    [Fact]
    public void Registry_DefaultEnabled_Has_Eight_Widgets()
    {
        Assert.Equal(8, DashboardWidgetRegistry.Defaults.Count);
    }

    [Fact]
    public void Registry_DefaultEnabled_ContainsExactlyExpectedIds()
    {
        var enabledIds = DashboardWidgetRegistry.Defaults.Select(w => w.Id).ToHashSet();

        // Expected enabled-by-default
        Assert.Contains(DashboardWidgetId.LibraryCoverage,       enabledIds);
        Assert.Contains(DashboardWidgetId.Volumes,               enabledIds);
        Assert.Contains(DashboardWidgetId.Systems,               enabledIds);
        Assert.Contains(DashboardWidgetId.Storage,               enabledIds);
        Assert.Contains(DashboardWidgetId.RecentActivitySummary, enabledIds);
        Assert.Contains(DashboardWidgetId.RecentOperations,      enabledIds);
        Assert.Contains(DashboardWidgetId.AttentionRequired,     enabledIds);
        Assert.Contains(DashboardWidgetId.ArchiveFormats,        enabledIds);

        // Expected disabled-by-default
        Assert.DoesNotContain(DashboardWidgetId.DiskHealth,   enabledIds);
        Assert.DoesNotContain(DashboardWidgetId.PendingWork,  enabledIds);
        Assert.DoesNotContain(DashboardWidgetId.RecentVolumes, enabledIds);
    }

    [Fact]
    public void Registry_Defaults_AreSortedByPriority()
    {
        var priorities = DashboardWidgetRegistry.Defaults.Select(w => w.Priority).ToList();
        Assert.Equal(priorities.OrderBy(p => p).ToList(), priorities);
    }

    // ── Mode resolution ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(699)]
    public void ResolveMode_BelowThreshold_ReturnsCompact(double width)
    {
        Assert.Equal(DashboardLayoutMode.Compact, DashboardLayoutEngine.ResolveMode(width));
    }

    [Theory]
    [InlineData(700)]
    [InlineData(900)]
    [InlineData(1099)]
    public void ResolveMode_MidRange_ReturnsStandard(double width)
    {
        Assert.Equal(DashboardLayoutMode.Standard, DashboardLayoutEngine.ResolveMode(width));
    }

    [Theory]
    [InlineData(1100)]
    [InlineData(1400)]
    [InlineData(2560)]
    public void ResolveMode_AboveThreshold_ReturnsWide(double width)
    {
        Assert.Equal(DashboardLayoutMode.Wide, DashboardLayoutEngine.ResolveMode(width));
    }

    // ── Widget selection ──────────────────────────────────────────────────────

    [Fact]
    public void Compact_ExcludesOptionalWidgets()
    {
        var engine  = new DashboardLayoutEngine();
        var visible = engine.ResolveWidgets(DashboardLayoutMode.Compact);
        var ids     = visible.Select(w => w.Id).ToHashSet();

        Assert.DoesNotContain(DashboardWidgetId.ArchiveFormats,  ids);
        Assert.DoesNotContain(DashboardWidgetId.DiskHealth,      ids);
        Assert.DoesNotContain(DashboardWidgetId.PendingWork,     ids);
        Assert.DoesNotContain(DashboardWidgetId.RecentVolumes,   ids);
    }

    [Fact]
    public void Compact_IncludesAllCoreSummaryAndMainWidgets()
    {
        var engine  = new DashboardLayoutEngine();
        var visible = engine.ResolveWidgets(DashboardLayoutMode.Compact);
        var ids     = visible.Select(w => w.Id).ToHashSet();

        Assert.Contains(DashboardWidgetId.LibraryCoverage,       ids);
        Assert.Contains(DashboardWidgetId.Volumes,               ids);
        Assert.Contains(DashboardWidgetId.Systems,               ids);
        Assert.Contains(DashboardWidgetId.Storage,               ids);
        Assert.Contains(DashboardWidgetId.RecentActivitySummary, ids);
        Assert.Contains(DashboardWidgetId.RecentOperations,      ids);
        Assert.Contains(DashboardWidgetId.AttentionRequired,     ids);
        Assert.Equal(7, visible.Count);
    }

    [Fact]
    public void Standard_IncludesArchiveFormats()
    {
        var engine  = new DashboardLayoutEngine();
        var visible = engine.ResolveWidgets(DashboardLayoutMode.Standard);
        var ids     = visible.Select(w => w.Id).ToHashSet();

        Assert.Contains(DashboardWidgetId.ArchiveFormats, ids);
        Assert.Equal(8, visible.Count);
    }

    [Fact]
    public void Wide_IncludesArchiveFormats()
    {
        var engine  = new DashboardLayoutEngine();
        var visible = engine.ResolveWidgets(DashboardLayoutMode.Wide);
        var ids     = visible.Select(w => w.Id).ToHashSet();

        Assert.Contains(DashboardWidgetId.ArchiveFormats, ids);
        Assert.Equal(8, visible.Count); // disabled-by-default not included
    }

    [Fact]
    public void DisabledWidgets_NeverAppear_InAnyMode()
    {
        var engine = new DashboardLayoutEngine();
        foreach (var mode in new[] { DashboardLayoutMode.Compact, DashboardLayoutMode.Standard, DashboardLayoutMode.Wide })
        {
            var ids = engine.ResolveWidgets(mode).Select(w => w.Id).ToHashSet();
            Assert.DoesNotContain(DashboardWidgetId.DiskHealth,    ids);
            Assert.DoesNotContain(DashboardWidgetId.PendingWork,   ids);
            Assert.DoesNotContain(DashboardWidgetId.RecentVolumes, ids);
        }
    }

    [Fact]
    public void CoreWidgets_AlwaysBeforeOptional_InPriorityOrder()
    {
        var engine  = new DashboardLayoutEngine();
        var visible = engine.ResolveWidgets(DashboardLayoutMode.Wide);

        // All Summary+Main widgets have lower priorities than Optional
        var coreMaxPriority = visible
            .Where(w => w.Group != DashboardWidgetGroup.Optional)
            .Max(w => w.Priority);

        var optionalMinPriority = visible
            .Where(w => w.Group == DashboardWidgetGroup.Optional)
            .Min(w => w.Priority);

        Assert.True(coreMaxPriority < optionalMinPriority);
    }

    [Fact]
    public void AllModes_ResultsAreSortedByPriority()
    {
        var engine = new DashboardLayoutEngine();
        foreach (var mode in new[] { DashboardLayoutMode.Compact, DashboardLayoutMode.Standard, DashboardLayoutMode.Wide })
        {
            var priorities = engine.ResolveWidgets(mode).Select(w => w.Priority).ToList();
            Assert.Equal(priorities.OrderBy(p => p).ToList(), priorities);
        }
    }

    [Fact]
    public void Engine_AcceptsCustomRegistry_ForTestability()
    {
        var custom = new List<DashboardWidgetDefinition>
        {
            new() { Id = "X", Priority = 1, Group = DashboardWidgetGroup.Summary, EnabledByDefault = true  },
            new() { Id = "Y", Priority = 2, Group = DashboardWidgetGroup.Optional, EnabledByDefault = true },
            new() { Id = "Z", Priority = 3, Group = DashboardWidgetGroup.Optional, EnabledByDefault = false },
        };

        var engine  = new DashboardLayoutEngine(custom);
        var compact = engine.ResolveWidgets(DashboardLayoutMode.Compact);
        var wide    = engine.ResolveWidgets(DashboardLayoutMode.Wide);

        Assert.Single(compact);
        Assert.Equal("X", compact[0].Id);

        Assert.Equal(2, wide.Count); // Z excluded (disabled)
        Assert.DoesNotContain(wide, w => w.Id == "Z");
    }
}
