using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Library;

namespace Arkadia.Catalog;

/// <summary>
/// Pure, UI-free filtering logic for the Catalog list.
///
/// Visibility rules:
///   "All Statuses" — include only entries that are visible in the default curated catalog:
///     status != "Unwanted" AND show_in_catalog == true
///   "Unwanted"    — include only entries with status "Unwanted" (explicit opt-in)
///   "Hidden"      — include only entries where show_in_catalog == false (explicit opt-in)
///   "New"         — newly introduced entries that pass the default visibility gate
///   Any other value — entries matching that status that also pass the default visibility gate
///
/// UNWANTED and hidden are independent flags:
///   - status == "Unwanted" means excluded from the wanted set
///   - show_in_catalog == false means hidden from the default catalog view
///   - Default catalog browsing hides both; each requires an explicit filter to appear
/// </summary>
public static class CatalogFilterService
{
    /// <summary>
    /// Applies search and status filter to <paramref name="source"/>.
    /// </summary>
    /// <param name="source">All entries for the active DAT-line dataset.</param>
    /// <param name="search">
    ///     Free-text search against DAT Name and metadata Title (case-insensitive).
    ///     Empty string disables search filtering.
    /// </param>
    /// <param name="statusFilter">
    ///     "All Statuses" — default visible catalog (excludes Unwanted and show_in_catalog=false).
    ///     "Unwanted"     — explicit opt-in: only unwanted entries.
    ///     "Hidden"       — explicit opt-in: only entries with show_in_catalog=false.
    ///     "New"          — newly introduced entries that pass the default visibility gate.
    ///     Any other string — entries matching that status, filtered by default visibility.
    /// </param>
    public static List<LibraryEntry> Apply(
        IEnumerable<LibraryEntry> source,
        string search,
        string statusFilter)
    {
        return source
            .Where(e => PassesVisibilityFilter(e, statusFilter))
            .Where(e => PassesSearch(e, search))
            .ToList();
    }

    // ── Visibility gate ───────────────────────────────────────────────────────

    private static bool PassesVisibilityFilter(LibraryEntry e, string filter) => filter switch
    {
        // Explicit opt-in: show only the unwanted bucket
        "Unwanted" => e.Status == "Unwanted",

        // Explicit opt-in: show only entries hidden from the default catalog
        "Hidden" => !e.ShowInCatalog,

        // Default: all visible catalog entries (exclude unwanted, exclude hidden)
        "All Statuses" => IsDefaultVisible(e),

        // "New" applies the default visibility gate additionally
        "New" => e.IsNew && IsDefaultVisible(e),

        // Any other status filter also respects the default visibility gate
        _ => e.Status == filter && IsDefaultVisible(e),
    };

    /// <summary>True when an entry should appear in the default catalog view.</summary>
    internal static bool IsDefaultVisible(LibraryEntry e)
        => e.Status != "Unwanted" && e.ShowInCatalog;

    // ── Search ────────────────────────────────────────────────────────────────

    private static bool PassesSearch(LibraryEntry e, string search)
        => search.Length == 0 ||
           e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
           (e.Metadata?.Title is { Length: > 0 } t &&
            t.Contains(search, StringComparison.OrdinalIgnoreCase));
}
