using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Arkadia.Themes.Assets;

/// <summary>Persisted shuffle-cycle state for a single asset family.</summary>
public sealed class FamilyCycleState
{
    /// <summary>Current shuffle order — filenames only, not full paths.</summary>
    [JsonPropertyName("order")]
    public List<string> Order { get; set; } = [];

    /// <summary>Next index to consume from <see cref="Order"/>.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }
}
