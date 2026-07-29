namespace Arkadia.Data;

public sealed class AppendCandidateInfo
{
    public required string DerivedArtifactId  { get; init; }
    public required string ContentIdentityKey { get; init; }
    public required string ReleaseName        { get; init; }
    public required string FileName           { get; init; }
    public required string RelativePath       { get; init; }
    public required long   SizeBytes          { get; init; }
    public required string ExpectedSha1       { get; init; }
}
