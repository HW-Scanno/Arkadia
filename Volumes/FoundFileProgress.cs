namespace Arkadia.Volumes;

/// <summary>
/// Progress event emitted for each active-area file discovered during a
/// recursive volume scan, before hash classification.
/// This is a neutral discovery event — it must not affect any verify counters.
/// </summary>
public sealed record FoundFileProgress(
    /// <summary>Path relative to the volume root, e.g. "some folder\Game.chd".</summary>
    string RelativePath,
    string FullPath,
    long   SizeBytes
);
