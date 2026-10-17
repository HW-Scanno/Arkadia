namespace Arkadia.Data;

/// <summary>
/// Pure resolver for the visible name of a DAT line. A stored <c>dat_lines.display_name</c> wins; when it is
/// NULL/blank (every legacy row) the historical derived label <c>"&lt;Authority&gt; · &lt;MEDIA&gt;"</c> is used, so
/// existing catalogs render exactly as before. The technical id is never an input: the label can change
/// without touching identity.
///
/// <para>The legacy format deliberately mirrors <c>MainWindow.FormatDatLineName</c>, which is kept for Group leaves
/// (their presentation is unchanged). Standalone (Single) DAT labels go through <see cref="Resolve"/>.</para>
/// </summary>
public static class DatLineDisplayName
{
    /// <summary>The legacy derived label, e.g. <c>"No-Intro · CARTRIDGE"</c>.</summary>
    public static string Legacy(string authorityName, string mediaTypeId)
        => $"{authorityName} · {mediaTypeId.ToUpperInvariant()}";

    /// <summary>Maximum persisted display-name length.</summary>
    public const int MaxLength = 100;

    /// <summary>
    /// Validates a display name for persistence (null = valid). A display name is a free human label: it must be
    /// non-blank after trimming, at most <see cref="MaxLength"/> characters, and free of control characters.
    /// It never feeds the technical id.
    /// </summary>
    public static string? Validate(string? displayName)
    {
        var t = displayName?.Trim() ?? "";
        if (t.Length == 0)          return "Display Name is required.";
        if (t.Length > MaxLength)   return $"Display Name is too long ({t.Length} characters; the maximum is {MaxLength}).";
        foreach (var c in t)
            if (char.IsControl(c))  return "Display Name must not contain control characters.";
        return null;
    }

    /// <summary>The value persisted for a valid display name (surrounding whitespace trimmed).</summary>
    public static string ToPersisted(string displayName) => displayName.Trim();

    /// <summary>
    /// Suggested initial display name from the authority's name and the variant, e.g. <c>("NoIntro", "main")</c> →
    /// <c>"NoIntro - Main"</c>, <c>("Local", "download-play")</c> → <c>"Local - Download Play"</c>. The variant is
    /// normalized for presentation only (never persisted as an id); a blank/unusable variant gives "Main".
    /// </summary>
    public static string Suggest(string? authorityName, string? variant)
    {
        var norm  = Arkadia.Data.Identifiers.DatTechnicalIdPolicy.NormalizeSuggestion(variant);
        if (norm.Length == 0) norm = Arkadia.Data.Identifiers.SingleDatIdBuilder.DefaultVariant;
        var parts = norm.Split('-', System.StringSplitOptions.RemoveEmptyEntries);
        var title = string.Join(' ', System.Linq.Enumerable.Select(parts, w => char.ToUpperInvariant(w[0]) + w[1..]));
        var a     = (authorityName ?? "").Trim();
        return a.Length > 0 ? $"{a} - {title}" : title;
    }

    /// <summary>Stored display name when non-blank (returned verbatim, not trimmed), otherwise <see cref="Legacy"/>.</summary>
    public static string Resolve(string? storedDisplayName, string authorityName, string mediaTypeId)
        => string.IsNullOrWhiteSpace(storedDisplayName)
            ? Legacy(authorityName, mediaTypeId)
            : storedDisplayName;
}
