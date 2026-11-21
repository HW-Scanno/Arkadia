using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>
/// Lookup of CRC-only DAT requirements ((size, crc32) → targets). Only requirements the SHA1/MD5 matcher can never
/// identify (<see cref="DatFileRequirementKind.Unmatchable"/>) with a valid CRC and a positive declared size are
/// indexed; anything that carries SHA1/MD5, declares size 0, or has no size is excluded. Pure — no filesystem, no DB.
/// </summary>
public sealed class CrcSizeIndex
{
    public static readonly CrcSizeIndex Empty = new(new());

    private readonly Dictionary<(long Size, string Crc), List<(string ReleaseId, string RomName)>> _map;
    private readonly HashSet<long> _sizes;

    private CrcSizeIndex(Dictionary<(long, string), List<(string, string)>> map)
    {
        _map   = map;
        _sizes = new HashSet<long>();
        foreach (var k in map.Keys) _sizes.Add(k.Item1);
    }

    public bool IsEmpty => _map.Count == 0;

    /// <summary>True when some CRC-only requirement declares exactly this size (cheap pre-filter before reading a file).</summary>
    public bool HasSize(long size) => _sizes.Contains(size);

    public bool TryGetTargets(long size, string crc, out List<(string ReleaseId, string RomName)> targets)
        => _map.TryGetValue((size, Crc32Hasher.Normalize(crc)), out targets!);

    public static CrcSizeIndex Build(
        IReadOnlyDictionary<string, ReleaseRecord>              releases,
        IReadOnlyDictionary<string, List<ReleaseFileRecord>>    allReleaseFiles)
    {
        var map = new Dictionary<(long, string), List<(string, string)>>();
        foreach (var (releaseId, files) in allReleaseFiles)
        {
            if (!releases.ContainsKey(releaseId)) continue;   // outdated / unknown releases never match
            foreach (var f in files)
            {
                if (DatFileRequirement.Classify(f) != DatFileRequirementKind.Unmatchable) continue;
                var crc = Crc32Hasher.Normalize(f.Crc);
                if (crc.Length == 0) continue;
                if (!long.TryParse(f.Size?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0) continue;

                var key = (size, crc);
                if (!map.TryGetValue(key, out var list)) map[key] = list = new();
                list.Add((releaseId, f.RomName));
            }
        }
        return new CrcSizeIndex(map);
    }
}

/// <summary>Outcome of one <see cref="CrcSizeFallbackMatcher.TryMatch"/>: accepted targets (empty = no match) + log operations.</summary>
public sealed record CrcSizeFallbackResult(
    IReadOnlyList<(string ReleaseId, string RomName)> Targets,
    IReadOnlyList<IngestionOperation>                 Operations)
{
    public static readonly CrcSizeFallbackResult None =
        new(Array.Empty<(string, string)>(), Array.Empty<IngestionOperation>());
}

/// <summary>
/// Last-resort identification of an incoming file against CRC-only DAT requirements, used ONLY after the SHA1 and MD5
/// matches both failed. A file is accepted when its size AND CRC32 equal a CRC-only requirement; the SHA1/MD5 already
/// computed from the accepted file are then persisted into the requirement's blank hash fields
/// (<see cref="DatLineStore.EnrichReleaseFileHashes"/>), turning it into an ordinary <see cref="DatFileRequirementKind.Physical"/>
/// requirement for the rest of the pipeline (commit identity, verification, future matching by SHA1).
///
/// <para>Safeguards: a release already satisfied (DB-authoritative) is never matched by CRC (so a CRC-only match can
/// never trigger a duplicate delete); unwanted releases are matched (so staging routes the file to incoming-skip) but
/// never enriched; DAT-declared hashes are never overwritten; two files with the same CRC32+size but different SHA1
/// in one run cannot both enrich the same requirement (first acceptance stands, the other is a logged conflict).</para>
/// </summary>
public sealed class CrcSizeFallbackMatcher
{
    private readonly CrcSizeIndex                          _index;
    private readonly IReadOnlyDictionary<string, ReleaseRecord> _releases;
    private readonly Func<string, bool>                    _isReleaseSatisfied;
    private readonly DatLineStore                          _store;
    private readonly Dictionary<string, string>            _acceptedSha1 = new(StringComparer.Ordinal);

    public CrcSizeFallbackMatcher(
        CrcSizeIndex                          index,
        IReadOnlyDictionary<string, ReleaseRecord> releases,
        Func<string, bool>                    isReleaseSatisfied,
        DatLineStore                          store)
    {
        _index              = index;
        _releases           = releases;
        _isReleaseSatisfied = isReleaseSatisfied;
        _store              = store;
    }

    public bool IsActive => !_index.IsEmpty;

    /// <param name="sha1">SHA1 of the candidate (lowercase hex), already computed by the primary matcher.</param>
    /// <param name="md5">MD5 of the candidate (lowercase hex), already computed by the primary matcher's fallback.</param>
    public CrcSizeFallbackResult TryMatch(string srcPath, string sha1, string md5)
    {
        if (_index.IsEmpty || sha1.Length != 40 || md5.Length != 32) return CrcSizeFallbackResult.None;

        long length;
        string crc;
        try
        {
            length = new FileInfo(srcPath).Length;
            if (!_index.HasSize(length)) return CrcSizeFallbackResult.None;   // no read unless a requirement can match
            crc = Crc32Hasher.ComputeHex(srcPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CrcSizeFallbackResult.None;
        }

        if (!_index.TryGetTargets(length, crc, out var candidates)) return CrcSizeFallbackResult.None;

        var targets = new List<(string ReleaseId, string RomName)>();
        var ops     = new List<IngestionOperation>();
        var fileName = Path.GetFileName(srcPath);

        foreach (var (releaseId, romName) in candidates)
        {
            if (!_releases.TryGetValue(releaseId, out var release)) continue;
            if (_isReleaseSatisfied(releaseId)) continue;   // never let a CRC-only match feed the duplicate-delete path

            if (release.Status == "unwanted")
            {
                targets.Add((releaseId, romName));          // UNWANTED WINS: routed to incoming-skip; DAT record untouched
                continue;
            }

            var key = $"{releaseId}|{romName}";
            if (_acceptedSha1.TryGetValue(key, out var prior))
            {
                if (string.Equals(prior, sha1, StringComparison.Ordinal))
                    targets.Add((releaseId, romName));      // byte-identical duplicate of an already accepted file
                else
                    ops.Add(new IngestionOperation(fileName, "dat-hash-conflict",
                        $"{release.Name}/{romName}: same CRC32+size as an accepted file but different SHA1 — not matched"));
                continue;
            }

            if (_store.EnrichReleaseFileHashes(releaseId, romName, length, crc, md5, sha1) != 1) continue;

            _acceptedSha1[key] = sha1;
            targets.Add((releaseId, romName));
            ops.Add(new IngestionOperation(fileName, "dat-hash-enriched",
                $"{release.Name}/{romName}: matched by CRC32 {crc} + size {length}; recorded SHA1 {sha1} and MD5 {md5}"));
        }

        return targets.Count == 0 && ops.Count == 0
            ? CrcSizeFallbackResult.None
            : new CrcSizeFallbackResult(targets, ops);
    }
}
