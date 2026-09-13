using System;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>Outcome of resolving the content identity under which one required file may be committed.</summary>
public sealed record FileCommitIdentity(
    bool                   CanCommit,
    string                 ContentIdentityKey,
    DatFileRequirementKind Kind,
    string                 FailureReason)
{
    public static FileCommitIdentity Ok(string key, DatFileRequirementKind kind) => new(true, key, kind, "");
    public static FileCommitIdentity Fail(DatFileRequirementKind kind, string reason) => new(false, "", kind, reason);
}

/// <summary>
/// Pure commit policy for file-oriented (per-file) storage strategies. Invariant: a required file that cannot be
/// committed is a HARD release failure — it is never silently skipped while the release goes on to become
/// <c>present</c>. The processor must register every non-committed, non-discarded required file as a release
/// failure; <see cref="IsReleaseFullyAccountedFor"/> is the final belt-and-braces check over the whole file set.
/// </summary>
public static class FileCommitIdentityPolicy
{
    /// <summary>
    /// Resolves the content identity for one required file. Physical files keep the historical DAT-declared key
    /// (<c>sha1:&lt;dat sha1&gt;</c>, else <c>md5:&lt;dat md5&gt;</c>) byte-for-byte. Authoritative hashless zero-byte
    /// files get a release-scoped key. Unmatchable and contradictory requirements cannot be committed.
    /// </summary>
    public static FileCommitIdentity Resolve(string releaseId, ReleaseFileRecord file)
    {
        var kind = DatFileRequirement.Classify(file);
        return kind switch
        {
            DatFileRequirementKind.SynthesizableEmpty =>
                FileCommitIdentity.Ok(DatFileRequirement.SynthesizedEmptyIdentityKey(releaseId, file.RomName), kind),
            DatFileRequirementKind.Unmatchable or DatFileRequirementKind.ContradictoryZeroByte =>
                FileCommitIdentity.Fail(kind, DatFileRequirement.DescribeUnsatisfiable(kind)),
            _ => FileCommitIdentity.Ok(
                file.Sha1.Length > 0 ? $"sha1:{file.Sha1}" : $"md5:{file.Md5}", kind),
        };
    }

    /// <summary>
    /// Physical verification of the promoted source file against its DAT requirement, before any commit. Returns
    /// null when acceptable, else the failure reason. A synthesizable empty requirement must be exactly 0 bytes; a
    /// requirement with a DAT SHA1 must hash-match (the historical rule, unchanged).
    /// </summary>
    public static string? VerifySource(ReleaseFileRecord file, DatFileRequirementKind kind, long sourceLength, string hashedSha1)
    {
        if (kind == DatFileRequirementKind.SynthesizableEmpty)
            return sourceLength == 0 ? null : $"expected an empty file (DAT size 0), found {sourceLength} bytes";

        if (file.Sha1.Length > 0 && !string.Equals(hashedSha1, file.Sha1, StringComparison.OrdinalIgnoreCase))
            return $"SHA1 mismatch: expected {Short(file.Sha1)}… got {Short(hashedSha1)}…";

        return null;
    }

    /// <summary>True only when every expected required file was committed or intentionally discarded by strategy.</summary>
    public static bool IsReleaseFullyAccountedFor(int expectedFileCount, int committedOrDiscardedCount)
        => expectedFileCount > 0 && committedOrDiscardedCount == expectedFileCount;

    private static string Short(string h) => h.Length >= 8 ? h[..8] : h;
}
