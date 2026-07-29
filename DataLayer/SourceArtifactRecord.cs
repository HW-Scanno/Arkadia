namespace Arkadia.Data;

/// <summary>
/// Proof that a good source file was physically seen and verified against
/// the DAT-declared checksums for a content identity.
/// No source path / source filename: this is provenance proof, not a file inventory.
/// </summary>
public sealed class SourceArtifactRecord
{
    public required string  Id                 { get; init; }
    public required string  ContentIdentityKey { get; init; }
    public required long    SourceSizeBytes    { get; init; }
    /// <summary>Physically computed SHA1 of the source file (always observed).</summary>
    public required string  HashedSourceSha1   { get; init; }
    /// <summary>Physically computed MD5, populated when DAT uses MD5 identity.</summary>
    public required string? HashedSourceMd5    { get; init; }
    /// <summary>Physically computed CRC32, populated when DAT uses CRC identity.</summary>
    public required string? HashedSourceCrc32  { get; init; }
    public required System.DateTime VerifiedAtUtc { get; init; }
}
