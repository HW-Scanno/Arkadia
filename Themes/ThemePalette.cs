using System.Collections.Generic;
using Avalonia.Media;

namespace Arkadia.Themes;

/// <summary>
/// Parses and validates raw palette entries from a theme manifest.
/// Only recognized role keys are accepted; values must be valid hex color strings.
/// Invalid entries are silently dropped — a bad palette never invalidates the theme.
/// </summary>
public static class ThemePalette
{
    /// <summary>The v1 set of recognized palette role keys.</summary>
    public static readonly IReadOnlySet<string> SupportedKeys = new HashSet<string>
    {
        "background",
        "surface",
        "surfaceAlt",
        "accent",
        "textPrimary",
        "textSecondary",
        "success",
        "warning",
        "error",
        "info",
    };

    /// <summary>
    /// Parses <paramref name="raw"/> into a validated palette.
    /// Only entries whose key is in <see cref="SupportedKeys"/> and whose value is a valid
    /// hex color string are included. Returns an empty dictionary when <paramref name="raw"/> is null.
    /// </summary>
    public static IReadOnlyDictionary<string, Color> Parse(Dictionary<string, string>? raw)
    {
        var result = new Dictionary<string, Color>();

        if (raw is null)
            return result;

        foreach (var (key, value) in raw)
        {
            if (!SupportedKeys.Contains(key))
                continue;

            if (Color.TryParse(value, out var color))
                result[key] = color;
        }

        return result;
    }
}
