namespace Arkadia.Data;

public sealed class ArtifactBuildInfo
{
    public required string DerivedArtifactId { get; init; }
    public required string ReleaseName       { get; init; }
    public required string FileName          { get; init; }
    public required string RelativePath      { get; init; }  // archive-relative, forward slashes
    public required long   SizeBytes         { get; init; }
    public required string ExpectedSha1      { get; init; }  // hashed_derived_sha1 from DB; never empty for valid records
}
