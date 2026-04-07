namespace Arkadia.Data;

/// <summary>
/// Build info extended with SHA1 for volume verification.
/// The expected file path within a volume root is:
///   SafeFileName(ReleaseName) / FileName
/// </summary>
public sealed class ArtifactVerifyInfo
{
    public required string DerivedArtifactId { get; init; }
    public required string ReleaseName       { get; init; }
    public required string FileName          { get; init; }
    public required long   SizeBytes         { get; init; }
    /// <summary>Expected SHA1 hex string, or "" if not recorded.</summary>
    public required string Sha1              { get; init; }
}
