namespace Arkadia.Ingestion;

public sealed record IngestionProgress
{
    public string              PhaseText       { get; init; } = "";
    public bool                IsIndeterminate { get; init; } = true;
    public int                 Total           { get; init; }
    // Null = "don't touch this counter" — lets phase-change/op-only reports
    // leave the header counters alone instead of resetting them to 0.
    public int?                Processed       { get; init; }
    public int?                Accepted        { get; init; }
    public int?                Rejected        { get; init; }
    /// <summary>Single new operation to append to the ops list. Null = counter-only update.</summary>
    public IngestionOperation? NewOperation    { get; init; }
}
