namespace Arkadia.Data;

/// <summary>
/// Build info extended with SHA1 for volume verification.
/// The expected file path within a volume root is:
///   FileName  (flat layout — no release-name subfolder)
/// Use <see cref="Volumes.VolumeArtifactPathBuilder.GetFlatFullPath"/> to build paths.
/// </summary>
public sealed class ArtifactVerifyInfo
{
    public required string DerivedArtifactId { get; init; }
    public required string ReleaseName       { get; init; }
    public required string FileName          { get; init; }
    public required long   SizeBytes         { get; init; }
    /// <summary>Expected SHA1 hex string, or "" if not recorded.</summary>
    public required string Sha1              { get; init; }
    /// <summary>
    /// Derived artifact path relative to the app root (from <c>derived_artifacts.relative_path</c>),
    /// e.g. <c>archive/dc/dc-redump-gd/Sonic Adventure (USA).chd</c>. Authoritative archive
    /// location regardless of layout (flat or legacy release-foldered). "" if not populated.
    /// </summary>
    public string RelativePath { get; init; } = "";
}
