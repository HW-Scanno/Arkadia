using System;

namespace Arkadia;

internal static class CatalogHeroHelpers
{
    /// <summary>
    /// Combines genre and subgenre into a single display label, or returns empty string when both are absent.
    /// </summary>
    internal static string FormatGenreLabel(string genre, string subgenre)
        => (genre, subgenre) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"Genre: {genre} / {subgenre}",
            ({ Length: > 0 }, _)               => $"Genre: {genre}",
            (_, { Length: > 0 })               => $"Genre: {subgenre}",
            _                                  => "",
        };

    /// <summary>
    /// Returns true when originalTitle is non-empty and meaningfully different from both the
    /// resolved display title and the raw DAT entry name.
    /// </summary>
    internal static bool ShouldShowOriginalTitle(
        string originalTitle, string displayTitle, string entryName)
        => originalTitle.Length > 0
        && !originalTitle.Equals(displayTitle, StringComparison.OrdinalIgnoreCase)
        && !originalTitle.Equals(entryName,    StringComparison.OrdinalIgnoreCase);
}
