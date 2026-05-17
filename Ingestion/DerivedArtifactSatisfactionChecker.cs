using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>
/// Decides whether an already-produced derived artifact is still valid so the
/// ingest pipeline can skip the expensive transform.
///
/// All conditions must hold:
///   1. Release status is "present"
///   2. A derived_artifacts DB row exists for this release
///   3. The physical CHD exists at the expected path
///   4. The physical file size is > 0
///   5. The DB row has a non-empty hashed_derived_sha1
///   6. The physical file's SHA1 matches the DB value (case-insensitive)
/// </summary>
internal static class DerivedArtifactSatisfactionChecker
{
    internal sealed record CheckResult(bool IsSatisfied, string Reason, DerivedArtifactRecord? Artifact);

    /// <param name="releaseStatus">Release.Status as loaded from the DB at run start.</param>
    /// <param name="artifacts">Derived artifact rows for this release (from GetDerivedArtifactsByReleaseId).</param>
    /// <param name="expectedPhysicalPath">Full path where the derived file should reside.</param>
    /// <param name="hasher">Optional SHA1 hasher override — inject in tests to avoid real I/O.</param>
    internal static CheckResult Check(
        string releaseStatus,
        IReadOnlyList<DerivedArtifactRecord> artifacts,
        string expectedPhysicalPath,
        Func<string, string>? hasher = null)
    {
        if (releaseStatus != "present")
            return new(false, $"release status is '{releaseStatus}', not 'present'", null);

        if (artifacts.Count == 0)
            return new(false, "no derived_artifacts row in DB", null);

        var artifact = artifacts[0];

        if (!File.Exists(expectedPhysicalPath))
            return new(false, "physical file missing", artifact);

        if (new FileInfo(expectedPhysicalPath).Length == 0)
            return new(false, "physical file is empty", artifact);

        if (artifact.HashedDerivedSha1.Length == 0)
            return new(false, "DB has no expected SHA1", artifact);

        var compute    = hasher ?? DefaultSha1;
        var actualHash = compute(expectedPhysicalPath);

        if (!string.Equals(actualHash, artifact.HashedDerivedSha1, StringComparison.OrdinalIgnoreCase))
        {
            var exp = artifact.HashedDerivedSha1.Length >= 8 ? artifact.HashedDerivedSha1[..8] : artifact.HashedDerivedSha1;
            var got = actualHash.Length >= 8 ? actualHash[..8] : actualHash;
            return new(false, $"hash mismatch (expected {exp}… got {got}…)", artifact);
        }

        return new(true, "release present, DB row verified, file exists, hash matches", artifact);
    }

    private static string DefaultSha1(string path)
    {
        using var fs  = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
