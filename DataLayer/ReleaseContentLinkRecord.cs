using System;

namespace Arkadia.Data;

/// <summary>
/// Structural link between a release and a per-file logical content identity.
/// Replaces the old release_artifacts → artifacts → artifact_transforms chain.
/// </summary>
public sealed class ReleaseContentLinkRecord
{
    public required string   Id                 { get; init; }
    public required string   ReleaseId          { get; init; }
    public required string   ContentIdentityKey { get; init; }
    public required DateTime CreatedAtUtc       { get; init; }
}
