namespace Arkadia.Data;

/// <summary>
/// Normalized working-state constants for catalog items.
/// Stored as TEXT in catalog_working_state.
/// </summary>
public static class WorkingState
{
    public const string Unknown    = "unknown";
    public const string Working    = "working";
    public const string Imperfect  = "imperfect";
    public const string NotWorking = "not_working";

    /// <summary>
    /// Maps a MAME driver status string to a catalog working state.
    ///   good        → working
    ///   imperfect   → imperfect
    ///   preliminary → not_working
    ///   (anything else) → unknown
    /// </summary>
    public static string FromMameDriverStatus(string? mameStatus) => mameStatus switch
    {
        "good"        => Working,
        "imperfect"   => Imperfect,
        "preliminary" => NotWorking,
        _             => Unknown,
    };
}

/// <summary>
/// One row in catalog_working_state.
/// ItemId is caller-defined; for MAME machines it is the machine short name (e.g. "sf2").
/// </summary>
public sealed record WorkingStateRecord(
    string  ItemId,
    string  State,
    string? Note,
    bool    IsManual);
