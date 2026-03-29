using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Arkadia.Themes.Assets;

/// <summary>
/// Full persisted shuffle state for all asset families.
/// Visual and audio branches track their active theme id and family cycles independently:
/// changing the visual theme resets only image cycles; changing the audio theme resets only sound cycles.
/// </summary>
public sealed class ShuffleState
{
    /// <summary>Active visual theme id — used to detect visual theme changes.</summary>
    [JsonPropertyName("visualThemeId")]
    public string VisualThemeId { get; set; } = "";

    /// <summary>Image family cycles — e.g. key "splash".</summary>
    [JsonPropertyName("imageCycles")]
    public Dictionary<string, FamilyCycleState> ImageCycles { get; set; } = new();

    /// <summary>Active audio theme id — used to detect audio theme changes.</summary>
    [JsonPropertyName("audioThemeId")]
    public string AudioThemeId { get; set; } = "";

    /// <summary>Sound family cycles — e.g. keys "startup", "mouse_click", "menu_click", "window_open".</summary>
    [JsonPropertyName("soundCycles")]
    public Dictionary<string, FamilyCycleState> SoundCycles { get; set; } = new();
}
