using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Arkadia.Themes;

/// <summary>Deserialized content of a theme's theme.json file.</summary>
public sealed class ThemeManifest
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>
    /// Optional raw color palette. Keys are role names (e.g. "background", "accent");
    /// values are hex color strings. Parsed and validated by <see cref="ThemePalette.Parse"/>.
    /// </summary>
    [JsonPropertyName("palette")]
    public Dictionary<string, string>? Palette { get; init; }
}
