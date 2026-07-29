namespace Arkadia.Data;

/// <summary>
/// Snapshot of progress for a running DAT import or update operation.
/// Reported from the background thread via IProgress&lt;DatOperationProgress&gt;.
/// </summary>
public sealed record DatOperationProgress
{
    public string PhaseText       { get; init; } = "";
    public bool   IsIndeterminate { get; init; } = true;
    public int    Total           { get; init; }
    public int    Processed       { get; init; }
    public int    Accepted        { get; init; }
    public int    Rejected        { get; init; }
    /// <summary>Name of the entry currently being processed, for live display. Empty = no update.</summary>
    public string CurrentItem     { get; init; } = "";
}
