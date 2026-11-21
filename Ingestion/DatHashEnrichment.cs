using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>
/// Keeps locally recorded SHA1/MD5 (see <see cref="CrcSizeFallbackMatcher"/>) alive across Update DAT, which rewrites a
/// release's <c>release_files</c> from the DAT. Pure.
/// </summary>
public static class DatHashEnrichment
{
    /// <summary>
    /// For each incoming (freshly parsed) file that declares NO md5 and NO sha1, copies the previous row's md5/sha1 when
    /// the previous row has both and the rom is unchanged (same name, size and CRC). Hashes supplied by the DAT always
    /// win and are never mixed with local ones; a changed rom (size/crc) carries nothing over.
    /// </summary>
    public static void PreserveEnrichedHashes(
        IReadOnlyList<ReleaseFileRecord> previous,
        IList<ReleaseFileRecord>         incoming)
    {
        if (previous.Count == 0) return;

        var byName = previous
            .Where(p => p.Sha1.Length > 0 && p.Md5.Length > 0)
            .GroupBy(p => p.RomName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var f in incoming)
        {
            if (f.Sha1.Length > 0 || f.Md5.Length > 0) continue;
            if (!byName.TryGetValue(f.RomName, out var p)) continue;
            if (!string.Equals(p.Size.Trim(), f.Size.Trim(), StringComparison.Ordinal)) continue;
            var crc = Crc32Hasher.Normalize(f.Crc);
            if (crc.Length == 0 || crc != Crc32Hasher.Normalize(p.Crc)) continue;

            f.Sha1 = p.Sha1;
            f.Md5  = p.Md5;
        }
    }
}
