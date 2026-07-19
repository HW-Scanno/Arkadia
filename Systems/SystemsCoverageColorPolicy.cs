namespace Arkadia.Systems;

/// <summary>
/// Single source of truth for the Systems <b>wanted-coverage</b> colour. Maps the wanted
/// coverage percent to a hex colour by threshold. UI-only — contains no calculation logic
/// and never changes coverage numbers.
///
/// Input is <see cref="SystemPlatform.WantedCoveragePercent"/> (nullable):
///   • <c>null</c> = <b>N/A</b> — there are no wanted releases (e.g. an all-unwanted system) → neutral grey.
///   • <c>0</c>    = wanted releases exist but none are present → <b>not</b> grey (dark blue), semantically
///                   distinct from N/A.
///
/// Thresholds (percent → colour):
///   100      → Cyan
///   80–99    → Light Green
///   60–79    → Green
///   40–59    → Yellow
///   20–39    → Orange
///   1–19     → Red
///   0        → Dark Blue
///   N/A      → Neutral Grey
///
/// Hex values reuse the existing Arkadia analytics palette for consistency.
/// </summary>
public static class SystemsCoverageColorPolicy
{
    public const string Cyan        = "#26C6DA"; // 100%
    public const string LightGreen  = "#9CCC65"; // 80–99%
    public const string Green       = "#4CAF50"; // 60–79%
    public const string Yellow      = "#FFD54F"; // 40–59%
    public const string Orange      = "#FF9800"; // 20–39%
    public const string Red         = "#EF5350"; // 1–19%
    public const string DarkBlue    = "#6B68EE"; // 0% (wanted exist, none present) — current equivalent
    public const string NeutralGray = "#9E9E9E"; // N/A (no wanted releases)

    /// <summary>Coverage colour (hex) for a wanted-coverage percent; <c>null</c> ⇒ N/A ⇒ neutral grey.</summary>
    public static string HexFor(int? wantedCoveragePercent)
    {
        if (wantedCoveragePercent is not { } pct)
            return NeutralGray;   // N/A — no wanted releases

        return pct switch
        {
            >= 100 => Cyan,
            >= 80  => LightGreen,
            >= 60  => Green,
            >= 40  => Yellow,
            >= 20  => Orange,
            >= 1   => Red,
            _      => DarkBlue,   // exactly 0% — wanted releases exist but none present
        };
    }
}
