using Arkadia.Ingestion;

namespace Arkadia.Systems;

public sealed record ImageCacheProgress
{
    public string              PhaseText       { get; init; } = "";
    public bool                IsIndeterminate { get; init; } = true;
    public int                 Total           { get; init; }
    public int                 Processed       { get; init; }
    public int                 Generated       { get; init; }
    public IngestionOperation? NewOperation    { get; init; }
}
