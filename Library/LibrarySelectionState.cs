using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Library;

/// <summary>
/// Tracks the selected Library release by its stable <see cref="LibraryEntry.ReleaseId"/>.
/// This is the single source of truth for selection — independent of list position or
/// collection identity, so it survives ItemsSource rebuilds and filter changes.
/// </summary>
public sealed class LibrarySelectionState
{
    private string? _selectedReleaseId;

    /// <summary>The currently selected release ID, or <c>null</c> when nothing is selected.</summary>
    public string? SelectedReleaseId => _selectedReleaseId;

    /// <summary>
    /// Records a user-initiated selection.
    /// Pass <c>null</c> to deselect.
    /// Returns <paramref name="entry"/> for call-site convenience.
    /// </summary>
    public LibraryEntry? Select(LibraryEntry? entry)
    {
        _selectedReleaseId = entry?.ReleaseId;
        return entry;
    }

    /// <summary>
    /// After the filtered list is rebuilt, resolves whether the previously selected
    /// release is still visible.
    /// <list type="bullet">
    ///   <item>Found → returns the matching entry (state preserved).</item>
    ///   <item>Not found → clears <see cref="SelectedReleaseId"/> and returns <c>null</c>.</item>
    ///   <item>Nothing was selected → returns <c>null</c> without touching state.</item>
    /// </list>
    /// </summary>
    public LibraryEntry? ResolveAfterFilter(IReadOnlyList<LibraryEntry> filteredEntries)
    {
        if (_selectedReleaseId is null) return null;

        var match = filteredEntries.FirstOrDefault(e => e.ReleaseId == _selectedReleaseId);
        if (match is null)
            _selectedReleaseId = null;   // selected release is no longer visible

        return match;
    }

    /// <summary>
    /// Clears the selection.
    /// Call this when the active dataset changes so that a stale ReleaseId from the
    /// previous dataset cannot accidentally match an entry in the new one.
    /// </summary>
    public void Clear() => _selectedReleaseId = null;
}
