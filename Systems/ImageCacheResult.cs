using System.Collections.Generic;
using Arkadia.Ingestion;

namespace Arkadia.Systems;

public sealed class ImageCacheResult
{
    public int                      SourcesProcessed { get; set; }
    public int                      CachedGenerated  { get; set; }
    public List<IngestionOperation> Operations       { get; } = new();
    public string?                  Error            { get; set; }
    public bool                     Success          => Error is null;
}
