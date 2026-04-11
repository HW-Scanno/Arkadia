namespace Arkadia.Ingestion;

public sealed record IngestionOperation(string Object, string Action, string Destination)
{
    /// <summary>Human-readable display label for the action column.</summary>
    public string Label => Action switch
    {
        "discarded-by-strategy" => "discarded",
        _                       => Action,
    };
}
