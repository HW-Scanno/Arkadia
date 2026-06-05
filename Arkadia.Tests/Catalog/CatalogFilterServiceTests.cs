using System.Collections.Generic;
using Arkadia.Catalog;
using Arkadia.Library;
using Xunit;

namespace Arkadia.Tests.Catalog;

/// <summary>
/// Tests for CatalogFilterService visibility rules.
///
/// Key invariants:
///   — "All Statuses" excludes Unwanted and show_in_catalog=false by default.
///   — "Unwanted" is an explicit opt-in that shows only the unwanted bucket.
///   — "Hidden" is an explicit opt-in that shows only show_in_catalog=false entries.
///   — Search does not bypass visibility; unwanted/hidden remain excluded.
/// </summary>
public sealed class CatalogFilterServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LibraryEntry Entry(string name, string status, bool showInCatalog = true,
        bool isNew = false)
    {
        var e = new LibraryEntry
        {
            Name          = name,
            Platform      = "Test",
            Status        = status,
            Region        = "",
            Languages     = "",
            Format        = "",
            Size          = "",
            Tier          = "",
            ShowInCatalog = showInCatalog,
        };
        return e;
    }

    // ── Test 1: CatalogDefault_ExcludesUnwanted ───────────────────────────────

    [Fact]
    public void CatalogDefault_ExcludesUnwanted()
    {
        var entries = new List<LibraryEntry>
        {
            Entry("Good Game",     "Present",  showInCatalog: true),
            Entry("Bad Game",      "Unwanted", showInCatalog: false),
            Entry("Missing Game",  "Missing",  showInCatalog: true),
        };

        var result = CatalogFilterService.Apply(entries, "", "All Statuses");

        Assert.DoesNotContain(result, e => e.Status == "Unwanted");
        Assert.Equal(2, result.Count);
    }

    // ── Test 2: CatalogDefault_ExcludesShowInCatalogFalse ────────────────────

    [Fact]
    public void CatalogDefault_ExcludesShowInCatalogFalse()
    {
        var entries = new List<LibraryEntry>
        {
            Entry("Visible",  "Present", showInCatalog: true),
            Entry("Hidden",   "Missing", showInCatalog: false),
        };

        var result = CatalogFilterService.Apply(entries, "", "All Statuses");

        Assert.DoesNotContain(result, e => !e.ShowInCatalog);
        var single = Assert.Single(result);
        Assert.Equal("Visible", single.Name);
    }

    // ── Test 3: CatalogIncludeUnwanted_ShowsOnlyUnwanted ─────────────────────

    [Fact]
    public void CatalogIncludeUnwanted_ShowsOnlyUnwanted()
    {
        var entries = new List<LibraryEntry>
        {
            Entry("Present Game",  "Present",  showInCatalog: true),
            Entry("Missing Game",  "Missing",  showInCatalog: true),
            Entry("Unwanted Game", "Unwanted", showInCatalog: false),
        };

        var result = CatalogFilterService.Apply(entries, "", "Unwanted");

        var single = Assert.Single(result);
        Assert.Equal("Unwanted", single.Status);
        Assert.Equal("Unwanted Game", single.Name);
    }

    // ── Test 4: CatalogIncludeHidden_ShowsHidden ──────────────────────────────

    [Fact]
    public void CatalogIncludeHidden_ShowsHidden()
    {
        var entries = new List<LibraryEntry>
        {
            Entry("Visible Present", "Present", showInCatalog: true),
            Entry("Hidden Missing",  "Missing", showInCatalog: false),
            Entry("Hidden Unwanted", "Unwanted", showInCatalog: false),
        };

        var result = CatalogFilterService.Apply(entries, "", "Hidden");

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.False(e.ShowInCatalog));
        Assert.DoesNotContain(result, e => e.ShowInCatalog);
    }

    // ── Test 5: CatalogAllDefault_DoesNotMeanTrulyAll ────────────────────────

    [Fact]
    public void CatalogAllDefault_DoesNotMeanTrulyAll()
    {
        // "All Statuses" in Catalog = "all visible wanted" — NOT a raw passthrough
        var entries = new List<LibraryEntry>
        {
            Entry("Present",  "Present",  showInCatalog: true),
            Entry("Missing",  "Missing",  showInCatalog: true),
            Entry("Lost",     "Lost",     showInCatalog: true),
            Entry("Unwanted", "Unwanted", showInCatalog: false),
            Entry("Hidden",   "Present",  showInCatalog: false),
        };

        var result = CatalogFilterService.Apply(entries, "", "All Statuses");

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, e => e.Status == "Unwanted");
        Assert.DoesNotContain(result, e => !e.ShowInCatalog);
    }

    // ── Test 6: CatalogSearch_DoesNotReturnHiddenUnwantedByDefault ───────────

    [Fact]
    public void CatalogSearch_DoesNotReturnHiddenUnwantedByDefault()
    {
        var entries = new List<LibraryEntry>
        {
            Entry("007 Agent (USA)",          "Present",  showInCatalog: true),
            Entry("007 Agent Under Fire",      "Unwanted", showInCatalog: false),
            Entry("007 Nightfire (USA)",       "Missing",  showInCatalog: false),
        };

        // Search for "007" — unwanted and hidden must still be excluded
        var result = CatalogFilterService.Apply(entries, "007", "All Statuses");

        var single = Assert.Single(result);
        Assert.Equal("007 Agent (USA)", single.Name);
    }

    // ── Test 7: RestoreWanted_ShowInCatalogTrue_ReturnsToCatalog ─────────────

    [Fact]
    public void RestoreWanted_ShowInCatalogTrue_ReturnsToCatalog()
    {
        // Simulate a release that was unwanted (hidden), then restored:
        // status → missing, show_in_catalog → true
        var entries = new List<LibraryEntry>
        {
            Entry("Restored Game", "Missing", showInCatalog: true),
            Entry("Still Unwanted", "Unwanted", showInCatalog: false),
        };

        var result = CatalogFilterService.Apply(entries, "", "All Statuses");

        var single = Assert.Single(result);
        Assert.Equal("Restored Game", single.Name);
        Assert.Equal("Missing", single.Status);
    }
}
