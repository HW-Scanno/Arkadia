using System;

namespace Arkadia.Data;

public sealed class VolumeArtifactRecord
{
    public required string   Id                 { get; init; }
    public required string   VolumeId           { get; init; }
    public required string   DatLineId          { get; init; }
    public required string   DerivedArtifactId  { get; init; }
    /// <summary>Logical DAT identity of this artifact; mirrors derived_artifacts.content_identity_key.</summary>
    public required string   ContentIdentityKey { get; init; }
    public required string   Status             { get; init; }  // "present_in_final" | "lost"
    public required DateTime AddedAtUtc         { get; init; }
}
