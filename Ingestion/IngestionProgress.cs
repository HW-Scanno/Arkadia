namespace Arkadia.Ingestion;

public sealed record IngestionProgress
{
    public string              PhaseText       { get; init; } = "";
    public bool                IsIndeterminate { get; init; } = true;
    public int                 Total           { get; init; }
    public int                 Processed       { get; init; }
    public int                 Accepted        { get; init; }
    public int                 Rejected        { get; init; }
    /// <summary>Single new operation to append to the ops list. Null = counter-only update.</summary>
    public IngestionOperation? NewOperation    { get; init; }
}
