using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Library;
using Xunit;

namespace Arkadia.Tests.Library;

/// <summary>
/// Regression suite for the Library selection/detail mismatch bug.
///
/// Root cause: ApplyLibraryFilter() called UpdateDetailPanel(null) unconditionally
/// after setting LibraryList.ItemsSource, racing with Avalonia's synchronous
/// SelectionChanged event and erasing the detail pane or leaving stale content.
///
/// The fix introduces:
///   LibraryFilterService  — pure, testable filter logic
///   LibrarySelectionState — selection tracked by ReleaseId (not list index)
/// </summary>
public sealed class LibraryFilterAndSelectionTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LibraryEntry MakeEntry(string releaseId, string status, string name = "") =>
        new()
        {
            Name      = name.Length > 0 ? name : $"Release {releaseId}",
            Platform  = "TestSystem",
            Status    = status,
            Region    = "USA",
            Languages = "EN",
            Format    = "CHD",
            Size      = "0 B",
            Tier      = "",
            ReleaseId = releaseId,
        };

    // ── LibraryFilterService ──────────────────────────────────────────────────

    [Fact]
    public void MissingFilter_ExcludesPresentEntries()
    {
        var entries = new[]
        {
            MakeEntry("1", "Missing"),
            MakeEntry("2", "Present"),
            MakeEntry("3", "Missing"),
            MakeEntry("4", "Present"),
        };

        var result = LibraryFilterService.Apply(entries, "", "Missing");

        Assert.DoesNotContain(result, e => e.Status == "Present");
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("Missing", e.Status));
    }

    [Fact]
    public void AllStatuses_PassesEveryEntry()
    {
        var entries = new[]
        {
            MakeEntry("1", "Present"),
            MakeEntry("2", "Missing"),
            MakeEntry("3", "Pending"),
            MakeEntry("4", "Lost"),
        };

        var result = LibraryFilterService.Apply(entries, "", "All Statuses");

        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void PresentFilter_ExcludesMissing()
    {
        var entries = new[]
        {
            MakeEntry("1", "Present"),
            MakeEntry("2", "Missing"),
            MakeEntry("3", "Present"),
        };

        var result = LibraryFilterService.Apply(entries, "", "Present");

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("Present", e.Status));
    }

    [Fact]
    public void SearchFilter_CaseInsensitive_FiltersByName()
    {
        var entries = new[]
        {
            MakeEntry("1", "Present", "Zelda Ocarina"),
            MakeEntry("2", "Present", "Mario Bros"),
            MakeEntry("3", "Missing", "zelda twilight"),
        };

        var result = LibraryFilterService.Apply(entries, "zelda", "All Statuses");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.ReleaseId == "1");
        Assert.Contains(result, e => e.ReleaseId == "3");
    }

    [Fact]
    public void SearchAndStatusFilter_Combined()
    {
        var entries = new[]
        {
            MakeEntry("1", "Missing", "Zelda Missing"),
            MakeEntry("2", "Present", "Zelda Present"),
            MakeEntry("3", "Missing", "Mario Missing"),
        };

        var result = LibraryFilterService.Apply(entries, "zelda", "Missing");

        Assert.Single(result);
        Assert.Equal("1", result[0].ReleaseId);
    }

    // ── LibrarySelectionState ─────────────────────────────────────────────────

    [Fact]
    public void FilterChange_SelectedItemNotVisible_ClearsSelection()
    {
        // Selecting a Present item then applying a Missing filter must clear the selection.
        var state   = new LibrarySelectionState();
        var present = MakeEntry("present-1", "Present");
        state.Select(present);

        // The Missing-only filtered list — the Present item is absent.
        var missingList = new[]
        {
            MakeEntry("missing-1", "Missing"),
            MakeEntry("missing-2", "Missing"),
        };

        var resolved = state.ResolveAfterFilter(missingList);

        Assert.Null(resolved);
        Assert.Null(state.SelectedReleaseId);
    }

    [Fact]
    public void FilterChange_SelectedItemStillVisible_KeepsSelection()
    {
        // If the selected Missing entry is still in the filtered result, it must be preserved.
        var state   = new LibrarySelectionState();
        var missing = MakeEntry("missing-5", "Missing");
        state.Select(missing);

        var filteredList = new[] { MakeEntry("missing-1", "Missing"), missing };
        var resolved     = state.ResolveAfterFilter(filteredList);

        Assert.NotNull(resolved);
        Assert.Equal("missing-5", resolved.ReleaseId);
        Assert.Equal("missing-5", state.SelectedReleaseId);
    }

    [Fact]
    public void DetailLoadsByReleaseId_NotSelectedIndex()
    {
        // Resolution must use ReleaseId identity, not the position in the list.
        var state  = new LibrarySelectionState();
        var target = MakeEntry("target-99", "Missing");
        state.Select(target);

        // Target is at index 2 in the new list — different from any plausible old index.
        var filteredList = new[]
        {
            MakeEntry("a", "Missing"),
            MakeEntry("b", "Missing"),
            target,                         // index 2
        };

        var resolved = state.ResolveAfterFilter(filteredList);

        Assert.Equal("target-99", resolved?.ReleaseId);
    }

    [Fact]
    public void RebuildFilteredList_DoesNotMismatchSelectionAndDetails()
    {
        // After RebuildLibraryDatasets creates brand-new LibraryEntry objects,
        // the old ReleaseId must not match a different entry in the new dataset.
        var state = new LibrarySelectionState();
        state.Select(MakeEntry("old-42", "Missing"));

        // New dataset has completely different ReleaseIds.
        var newDataset = new[]
        {
            MakeEntry("new-1", "Missing"),
            MakeEntry("new-2", "Missing"),
        };

        var resolved = state.ResolveAfterFilter(newDataset);

        Assert.Null(resolved);
        Assert.Null(state.SelectedReleaseId);
    }

    [Fact]
    public void SelectionByReleaseId_StableAcrossSortOrder()
    {
        // The selected release must be found regardless of its position in the list.
        var state  = new LibrarySelectionState();
        var target = MakeEntry("target-42", "Missing");
        state.Select(target);

        // Same entries, different order.
        var reordered = new[]
        {
            MakeEntry("z-99", "Missing"),
            MakeEntry("a-01", "Missing"),
            target,
            MakeEntry("m-50", "Missing"),
        };

        var resolved = state.ResolveAfterFilter(reordered);

        Assert.Equal("target-42", resolved?.ReleaseId);
    }

    [Fact]
    public void PresentEntryCannotRemainSelectedUnderMissingFilter()
    {
        // Full pipeline: filter + selection resolution must agree — no Present
        // entry can survive as the active selection when filter = "Missing".
        var state   = new LibrarySelectionState();
        var present = MakeEntry("present-1", "Present");
        state.Select(present);

        var allEntries = new[]
        {
            present,
            MakeEntry("missing-1", "Missing"),
            MakeEntry("missing-2", "Missing"),
        };

        // Apply the Missing filter (same logic as ApplyLibraryFilter in MainWindow).
        var filtered = LibraryFilterService.Apply(allEntries, "", "Missing");

        // Resolve selection against the filtered list.
        var resolved = state.ResolveAfterFilter(filtered);

        Assert.Null(resolved);
        Assert.Null(state.SelectedReleaseId);
        Assert.DoesNotContain(filtered, e => e.Status == "Present");
    }

    [Fact]
    public void ClearResetsState()
    {
        var state = new LibrarySelectionState();
        state.Select(MakeEntry("x", "Missing"));

        state.Clear();

        Assert.Null(state.SelectedReleaseId);

        // After Clear, even if the entry is in the filtered list, ResolveAfterFilter
        // returns null — the dataset-change semantics require a fresh selection.
        var resolved = state.ResolveAfterFilter(new[] { MakeEntry("x", "Missing") });
        Assert.Null(resolved);
    }

    [Fact]
    public void SelectNull_ClearsReleaseId()
    {
        var state = new LibrarySelectionState();
        state.Select(MakeEntry("1", "Missing"));

        state.Select(null);

        Assert.Null(state.SelectedReleaseId);
    }

    [Fact]
    public void ResolveAfterFilter_NothingSelectedInitially_ReturnsNull()
    {
        var state      = new LibrarySelectionState();
        var filtered   = new[] { MakeEntry("1", "Missing") };
        var resolved   = state.ResolveAfterFilter(filtered);

        Assert.Null(resolved);
        Assert.Null(state.SelectedReleaseId);
    }
}
