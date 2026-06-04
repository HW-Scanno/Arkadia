using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Library;

/// <summary>
/// Pure, UI-free filtering logic for the Library list.
/// Extracted from MainWindow for testability.
/// </summary>
public static class LibraryFilterService
{
    /// <summary>
    /// Applies name search and status filter to <paramref name="source"/>.
    /// </summary>
    /// <param name="source">All entries for the active DAT-line dataset.</param>
    /// <param name="search">
    ///     Free-text search against Name and DisplayName (case-insensitive).
    ///     Empty string disables the search filter.
    /// </param>
    /// <param name="statusFilter">
    ///     "All Statuses" passes everything.
    ///     "New" passes entries where <see cref="LibraryEntry.IsNew"/> is true.
    ///     "Hidden" passes entries where <see cref="LibraryEntry.ShowInCatalog"/> is false.
    ///     Any other value is matched exactly against <see cref="LibraryEntry.Status"/>.
    /// </param>
    public static List<LibraryEntry> Apply(
        IEnumerable<LibraryEntry> source,
        string search,
        string statusFilter)
    {
        return source
            .Where(e => search.Length == 0 ||
                        e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        e.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(e => statusFilter == "All Statuses"
                     || (statusFilter == "New"    ? e.IsNew
                       : statusFilter == "Hidden" ? !e.ShowInCatalog
                       : e.Status == statusFilter))
            .ToList();
    }
}
