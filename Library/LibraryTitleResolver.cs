using System.Text.RegularExpressions;

namespace Arkadia.Library;

public static class LibraryTitleResolver
{
    private static readonly Regex TrailingBrackets =
        new(@"(?:\s*\([^)]*\))+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns the display title for a library entry.
    /// In "dat" mode (or when no metadata exists) the raw DAT name is returned unchanged.
    /// In "catalog" mode the metadata title is used; if it has no trailing bracket and the raw
    /// name does, the raw name's trailing bracket(s) are appended.
    /// </summary>
    public static string Resolve(string rawName, string titleMode, string? metadataTitle)
    {
        if (titleMode != "catalog" || string.IsNullOrEmpty(metadataTitle))
            return rawName;

        // Metadata already carries its own bracket — return as-is
        if (TrailingBrackets.IsMatch(metadataTitle))
            return metadataTitle;

        // Append trailing bracket(s) from the raw name if present
        var m = TrailingBrackets.Match(rawName);
        return m.Success ? metadataTitle + m.Value : metadataTitle;
    }
}
