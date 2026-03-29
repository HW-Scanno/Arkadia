using System.Collections.Generic;
using Avalonia.Media;

namespace Arkadia.Themes;

/// <summary>
/// Describes a discovered theme directory and its validation state.
/// <para>
/// IsRuntimeValid — the theme folder exists; usable as a last-resort runtime fallback
/// even when theme.json is absent or malformed.
/// </para>
/// <para>
/// IsSelectable — theme.json is present, is valid JSON, and contains a non-empty id and name;
/// safe to present to the user for selection.
/// </para>
/// </summary>
public sealed class ThemeDescriptor
{
    public required string ThemeId { get; init; }
    public required string ThemeDirectory { get; init; }
    public bool IsRuntimeValid { get; init; }
    public bool IsSelectable { get; init; }
    public ThemeManifest? Manifest { get; init; }

    /// <summary>
    /// Validated color palette parsed from the theme manifest.
    /// Contains only entries with recognized role keys and valid hex color values.
    /// Empty when the manifest has no palette or the palette contains no valid entries.
    /// </summary>
    public IReadOnlyDictionary<string, Color> Palette { get; init; } =
        new Dictionary<string, Color>();
}
