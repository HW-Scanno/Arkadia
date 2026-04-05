using System.Collections.Generic;

namespace Arkadia.Ingestion;

public sealed class IngestionResult
{
    public int                      FilesScanned    { get; set; }
    public int                      FilesMatched    { get; set; }
    public int                      FilesCopied     { get; set; }
    public int                      ReleasesPresent { get; set; }
    public int                      FilesSkipped    { get; set; }
    public List<IngestionOperation> Operations      { get; } = new();
    /// <summary>Non-null only for hard failures that aborted the pipeline.</summary>
    public string?                  Error           { get; set; }
    public bool                     Success         => Error is null;
}
