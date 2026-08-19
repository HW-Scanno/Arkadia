using System;
using System.Collections.Generic;
using System.IO;

namespace Arkadia.Systems;

/// <summary>
/// One efficient, per-platform staging probe. STAGING is a LIVE-FILESYSTEM attention flag (intentionally derived
/// from disk, not the DB): a leaf dat line has staging material when its staging root
/// (<c>staging/&lt;platformId&gt;/&lt;datLineId&gt;</c>) contains any file. The snapshot enumerates the platform's
/// staging roots once and short-circuits on the first file under each — no recursion cost beyond the first hit, no
/// file contents read, no hashing. Callers map the returned leaf ids to Single-DAT / Group ownership to derive
/// child and parent flags, so a big group never triggers hundreds of independent tree scans just to render a card.
/// Pure read-only; missing directories yield an empty set.
/// </summary>
public static class StagingSnapshot
{
    /// <summary>
    /// The set of leaf dat-line ids (the staging-root directory names) that currently hold staging material under
    /// <c>staging/&lt;platformId&gt;</c>. Empty when the platform has no staging directory. Never throws for a
    /// missing/inaccessible tree — such roots are simply treated as empty.
    /// </summary>
    public static HashSet<string> LeafIdsWithStaging(string appRoot, string platformId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var root   = Path.Combine(appRoot, "staging", platformId);
        if (!Directory.Exists(root)) return result;

        IEnumerable<string> leafDirs;
        try { leafDirs = Directory.EnumerateDirectories(root); }
        catch { return result; }

        foreach (var leafDir in leafDirs)
            if (HasAnyFile(leafDir))
                result.Add(Path.GetFileName(leafDir));

        return result;
    }

    /// <summary>True if any file exists anywhere beneath <paramref name="dir"/>; stops at the first one found.</summary>
    private static bool HasAnyFile(string dir)
    {
        try
        {
            using var e = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).GetEnumerator();
            return e.MoveNext();
        }
        catch { return false; }
    }
}
