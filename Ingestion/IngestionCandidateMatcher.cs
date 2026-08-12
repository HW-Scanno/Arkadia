using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Arkadia.Ingestion;

/// <summary>
/// Outcome of hashing and matching one incoming candidate against a hash index. Generic in the target type
/// so the same matcher serves the Single-DAT run (target = (releaseId, romName)) and a future Group run
/// (target = a leaf-qualified triple) with no type change. Distinguishes the two zero-target cases that the
/// historical Single pipeline conflates: <see cref="HashSucceeded"/> false = the file could not be read/
/// hashed; <see cref="HashSucceeded"/> true with <see cref="MatchCount"/> 0 = hashed fine but nothing
/// matched. <see cref="Filtered"/> = a supplied SHA1 filter (Repair) rejected it before the MD5 fallback.
/// </summary>
public sealed class IncomingCandidateMatch<T>
{
    public required string  SourcePath    { get; init; }
    public required bool    HashSucceeded { get; init; }
    public required bool    Filtered      { get; init; }
    public          string  Sha1          { get; init; } = "";
    public          string  Md5           { get; init; } = "";
    public required IReadOnlyList<T> Targets { get; init; }

    public int MatchCount => Targets.Count;
}

/// <summary>
/// Extracts the historical per-file hash+match step (Phase 2/3) into a reusable primitive. Behavior is
/// preserved exactly: SHA1 in one stream pass; MD5 computed (a second read) ONLY when SHA1 produced no
/// match; the optional <paramref name="shouldIngest"/> SHA1 filter is applied BEFORE the MD5 fallback (so
/// the number of physical reads is unchanged). No hashing optimization, no combined SHA1+MD5 pass, no cache.
/// </summary>
public static class IngestionCandidateMatcher
{
    public static IncomingCandidateMatch<T> HashAndMatch<T>(
        string                                     srcPath,
        IReadOnlyDictionary<string, List<T>>       sha1Index,
        IReadOnlyDictionary<string, List<T>>       md5Index,
        Func<string, bool>?                        shouldIngest = null)
    {
        string sha1 = "";
        string md5  = "";

        try
        {
            using var fs = File.OpenRead(srcPath);
            sha1 = Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
        }
        catch { /* unreadable — hash failed */ }

        // Repair filter: reject before MD5 so read count matches the historical pipeline.
        if (shouldIngest != null && sha1.Length > 0 && !shouldIngest(sha1))
            return new IncomingCandidateMatch<T>
            {
                SourcePath = srcPath, HashSucceeded = true, Filtered = true,
                Sha1 = sha1, Targets = Array.Empty<T>(),
            };

        IReadOnlyList<T> targets = Array.Empty<T>();

        if (sha1.Length > 0 && sha1Index.TryGetValue(sha1, out var sha1Matches))
        {
            targets = sha1Matches;
        }
        else if (sha1.Length > 0)
        {
            try
            {
                using var fs = File.OpenRead(srcPath);
                md5 = Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
            }
            catch { }

            if (md5.Length > 0 && md5Index.TryGetValue(md5, out var md5Matches))
                targets = md5Matches;
        }

        return new IncomingCandidateMatch<T>
        {
            SourcePath    = srcPath,
            HashSucceeded = sha1.Length > 0,
            Filtered      = false,
            Sha1          = sha1,
            Md5           = md5,
            Targets       = targets,
        };
    }
}
