using System;

namespace Arkadia.Data;

/// <summary>
/// Canonical persisted archive artifact.
/// Physical hashes are independently observed from the stored derived file,
/// not blindly copied from source (even for no_compression where they happen to match).
/// </summary>
public sealed class DerivedArtifactRecord
{
    public string    Id                  { get; set; } = "";
    public string    StorageStrategyId   { get; set; } = "";
    /// <summary>ID of the source_artifacts row that proved this content was verified.</summary>
    public string    SourceArtifactId    { get; set; } = "";
    public string    ContentIdentityKey  { get; set; } = "";
    public string    FileName            { get; set; } = "";
    public string    RelativePath        { get; set; } = "";
    public long      DerivedSizeBytes    { get; set; }
    /// <summary>Physically computed SHA1 of the derived file (always observed).</summary>
    public string    HashedDerivedSha1   { get; set; } = "";
    /// <summary>Physically computed MD5 of the derived file, or null.</summary>
    public string?   HashedDerivedMd5    { get; set; }
    /// <summary>Physically computed CRC32 of the derived file, or null.</summary>
    public string?   HashedDerivedCrc32  { get; set; }
    public string    Status              { get; set; } = "";
    public DateTime  CreatedAtUtc        { get; set; }
    public DateTime? VerifiedAtUtc       { get; set; }
    public string    ArchiveTier         { get; set; } = "B";
}
