using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Arkadia.Providers;

/// <summary>
/// Sanitises ScreenScraper payload JSON before it is persisted to disk or ZIP.
/// Two transformations are applied in order:
/// <list type="number">
///   <item>Remove <c>response.ssuser</c> — account-specific quota data, not needed offline.</item>
///   <item>Replace credential values in URL query parameters with fixed placeholders.</item>
/// </list>
/// If the input is not valid JSON the first step is skipped and only the regex
/// replacement is applied, so the method never throws on malformed input.
/// </summary>
public static class ScreenScraperPayloadSanitizer
{
    // Matches [?&]sensitiveParam=value, stopping at the next &, ", or \
    private static readonly Regex SensitiveRx = new(
        @"([?&](devid|devpassword|ssid|sspassword|softname)=)[^&""\\]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> Placeholders =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["devid"]       = "<DEVID>",
            ["devpassword"] = "<DEVPASSWORD>",
            ["ssid"]        = "<SSID>",
            ["sspassword"]  = "<SSPASSWORD>",
            ["softname"]    = "<SOFTNAME>",
        };

    // Preserve non-ASCII characters (game names, etc.) and URL-special chars as-is.
    // Without this, System.Text.Json would escape '&' as \u0026, breaking the URL regex.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Returns a sanitised copy of <paramref name="json"/>:
    /// <c>response.ssuser</c> is removed and sensitive credential query-parameter
    /// values are replaced by their corresponding placeholders.
    /// </summary>
    public static string SanitizeJson(string json)
    {
        if (json.Length == 0) return json;

        string stripped;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject root &&
                root["response"] is JsonObject response)
            {
                response.Remove("ssuser");
            }
            stripped = node?.ToJsonString(JsonOpts) ?? json;
        }
        catch
        {
            // Fallback: JSON parse failed — apply only credential regex.
            stripped = json;
        }

        return SensitiveRx.Replace(stripped, m =>
            m.Groups[1].Value + Placeholders[m.Groups[2].Value]);
    }
}
