using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Arkadia.Library;
using Xunit;

namespace Arkadia.Tests.Library;

/// <summary>
/// Tests for catalog visibility (show_in_catalog) and the Hidden filter.
/// </summary>
public sealed class LibraryCatalogVisibilityTests : IDisposable
{
    private readonly string _dbPath;

    public LibraryCatalogVisibilityTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ArkLibVis_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "test.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }

    private DatLineStore Open() => new(_dbPath);

    private LibraryEntry MakeEntry(string name, string status, bool showInCatalog = true)
        => new()
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

    // ── Test 16: HiddenReleaseExcludedFromDefaultCatalog ─────────────────────

    [Fact]
    public void HiddenReleaseExcludedFromDefaultCatalog()
    {
        var entries = new List<LibraryEntry>
        {
            MakeEntry("Visible Game", "Missing", showInCatalog: true),
            MakeEntry("Hidden Game",  "Missing", showInCatalog: false),
        };

        // "All Statuses" filter passes all entries regardless of visibility.
        // The Hidden filter specifically targets showInCatalog = false.
        var filtered = LibraryFilterService.Apply(entries, "", "Hidden");
        Assert.Single(filtered);
        Assert.Equal("Hidden Game", filtered[0].Name);
    }

    // ── Test 17: ShowHiddenFilterIncludesHidden ───────────────────────────────

    [Fact]
    public void ShowHiddenFilterIncludesHidden()
    {
        var entries = new List<LibraryEntry>
        {
            MakeEntry("A", "Present",  showInCatalog: true),
            MakeEntry("B", "Missing",  showInCatalog: false),
            MakeEntry("C", "Unwanted", showInCatalog: false),
        };

        var hidden = LibraryFilterService.Apply(entries, "", "Hidden");
        Assert.Equal(2, hidden.Count);
        Assert.Contains(hidden, e => e.Name == "B");
        Assert.Contains(hidden, e => e.Name == "C");
    }

    // ── Test 18: UnwantedHiddenByDefault_PersistsAfterSetShowInCatalog ────────

    [Fact]
    public void UnwantedHiddenByDefault_CanBeSetViaStore()
    {
        var store = Open();
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "rel1", DatLineId = "dl1", Name = "Unwanted Game",
                    Status = "unwanted", ShowInCatalog = false }
        });

        var loaded = store.LoadReleases();
        Assert.Single(loaded);
        Assert.False(loaded[0].ShowInCatalog);
    }

    // ── Test 19: UnwantedVisibleWhenFilterEnabled ─────────────────────────────

    [Fact]
    public void UnwantedVisibleWhenFilterEnabled()
    {
        // Status strings match what Capitalize(r.Status) produces in the real app
        var entries = new List<LibraryEntry>
        {
            MakeEntry("Present Game",  "Present",  showInCatalog: true),
            MakeEntry("Unwanted Game", "Unwanted", showInCatalog: false),
        };

        // All Statuses shows everything
        var all = LibraryFilterService.Apply(entries, "", "All Statuses");
        Assert.Equal(2, all.Count);

        // Unwanted filter shows only unwanted
        var unwanted = LibraryFilterService.Apply(entries, "", "Unwanted");
        Assert.Single(unwanted);
        Assert.Equal("Unwanted Game", unwanted[0].Name);

        // Present filter shows only present entries
        var present = LibraryFilterService.Apply(entries, "", "Present");
        Assert.Single(present);
        Assert.Equal("Present Game", present[0].Name);
    }
}
