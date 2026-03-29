using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkadia.Themes;

/// <summary>
/// Minimal config hook for active visual and audio theme selection.
/// Visual and audio themes are independent — each has its own active id and default fallback.
/// </summary>
public sealed class ThemeSettings
{
    [JsonPropertyName("activeVisualThemeId")]
    public string ActiveVisualThemeId { get; init; } = "default";

    [JsonPropertyName("activeAudioThemeId")]
    public string ActiveAudioThemeId { get; init; } = "default";

    /// <summary>
    /// Loads settings from a JSON file at <paramref name="configPath"/>.
    /// Returns defaults ("default" for both branches) if the file is missing or invalid.
    /// </summary>
    public static ThemeSettings Load(string configPath)
    {
        if (!File.Exists(configPath))
            return new ThemeSettings();

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<ThemeSettings>(json) ?? new ThemeSettings();
        }
        catch (JsonException)
        {
            return new ThemeSettings();
        }
    }
}
