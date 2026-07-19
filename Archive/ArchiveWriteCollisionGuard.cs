using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Archive;

/// <summary>Decision for whether an archive writer may write to a target path.</summary>
public enum ArchiveWriteDecision
{
    /// <summary>Target does not exist — write normally (Case A).</summary>
    AllowWrite,
    /// <summary>Target exists and belongs to the SAME content identity — idempotent reuse/rebuild is safe (Case B).</summary>
    SameIdentityReuse,
    /// <summary>Target exists and belongs to a DIFFERENT content identity — collision, must not overwrite (Case C).</summary>
    CollisionDifferentIdentity,
    /// <summary>Target exists but no DB artifact claims it — identity unknown, block conservatively (Case D).</summary>
    UnknownExistingBlock,
}

/// <summary>
/// Defense-in-depth guard consulted immediately before an ingestion processor
/// writes (or reuses) an archive target. Pure/DB-free: the caller supplies the
/// content-identity keys already recorded at the target relative_path (from
/// <c>derived_artifacts</c>) and the identity it is about to write.
///
/// Identity signal = <c>content_identity_key</c> (the DAT-declared logical identity,
/// e.g. "sha1:…" for per-file outputs, "release:{id}" for release-level outputs).
/// Two different releases that resolve to the same archive filename carry different
/// content identities, so a collision is detected before any overwrite/reuse.
/// </summary>
public static class ArchiveWriteCollisionGuard
{
    public static ArchiveWriteDecision Decide(
        bool targetExists,
        IReadOnlyCollection<string> existingContentKeys,
        string expectedContentKey)
    {
        if (!targetExists)
            return ArchiveWriteDecision.AllowWrite;

        if (existingContentKeys.Count == 0)
            return ArchiveWriteDecision.UnknownExistingBlock;

        return existingContentKeys.Any(k => string.Equals(k, expectedContentKey, StringComparison.Ordinal))
            ? ArchiveWriteDecision.SameIdentityReuse
            : ArchiveWriteDecision.CollisionDifferentIdentity;
    }

    /// <summary>True when the decision must prevent any write/reuse of the target.</summary>
    public static bool IsBlocking(ArchiveWriteDecision decision)
        => decision is ArchiveWriteDecision.CollisionDifferentIdentity
                    or ArchiveWriteDecision.UnknownExistingBlock;
}
