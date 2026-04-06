namespace Arkadia.Data;

public sealed class ArtifactBuildInfo
{
    public required string DerivedArtifactId { get; init; }
    public required string ReleaseName       { get; init; }
    public required string FileName          { get; init; }
    public required string RelativePath      { get; init; }  // archive-relative, forward slashes
    public required long   SizeBytes         { get; init; }
}
