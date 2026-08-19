using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Arkadia.Ingestion;

/// <summary>
/// Shared, single-source path helpers for the ingestion pipeline. Extracted so
/// stale-cleanup logic derives release folder names and collision-safe
/// destinations with the EXACT same rules the pipeline used to create them —
/// no reimplementation, no drift.
/// </summary>
public static class IngestionPaths
{
    /// <summary>
    /// Sanitizes a release name into a filesystem-safe folder segment.
    /// Invalid characters become '_', leading/trailing '_'/spaces are trimmed,
    /// and an empty result falls back to "release". This is the authority for
    /// <c>staging\&lt;platform&gt;\&lt;datLine&gt;\&lt;folder&gt;</c> and
    /// <c>source\...\&lt;folder&gt;</c> naming.
    /// </summary>
    public static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var sanitized = sb.ToString().Trim('_', ' ');
        return sanitized.Length > 0 ? sanitized : "release";
    }

    /// <summary>
    /// Resolves a DAT <c>rom.name</c> (which may be a relative path <b>inside a release</b>, e.g.
    /// <c>TIME-PILOT\assets\bank001.au</c>) to a native filesystem path strictly contained below
    /// <paramref name="releaseRoot"/>, or fails. This is the single seam for turning the preserved DAT relative
    /// path into a filesystem location for staging / promotion / presence checks; it does the separator and
    /// containment work at the filesystem boundary only — it never rewrites the persisted <c>rom_name</c> and
    /// never touches the filesystem.
    ///
    /// Rules (all platform-independent): both <c>\</c> and <c>/</c> are logical DAT separators; empty input,
    /// empty segments (absolute / leading or doubled separators), <c>.</c>/<c>..</c> segments (traversal), and
    /// segments containing <c>:</c> (drive / alternate-data-stream) are rejected; a robust canonical containment
    /// check (directory-boundary aware — <c>root</c> vs <c>root-evil</c> can never confuse it) is applied last as
    /// defence in depth. Returns <c>false</c> (never throws) so read-only callers can treat an invalid/escaping
    /// name as simply "not present".
    /// </summary>
    public static bool TryResolveReleaseChild(string releaseRoot, string romName, out string nativePath)
    {
        nativePath = "";
        if (string.IsNullOrEmpty(releaseRoot) || string.IsNullOrWhiteSpace(romName))
            return false;

        var segments = romName.Split('\\', '/');   // both are logical DAT separators, regardless of OS
        foreach (var seg in segments)
        {
            if (seg.Length == 0)            return false;   // leading/doubled separator ⇒ absolute/malformed
            if (seg == "." || seg == "..")  return false;   // no-op / traversal
            if (seg.IndexOf(':') >= 0)      return false;   // drive-relative (C:) or alternate-data-stream
        }

        string combined, fullRoot, fullDest;
        try
        {
            combined = Path.Combine(new[] { releaseRoot }.Concat(segments).ToArray());
            fullRoot = Path.GetFullPath(releaseRoot);
            fullDest = Path.GetFullPath(combined);
        }
        catch { return false; }   // invalid characters etc. — reject conservatively

        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        // Strict containment: the child must live BELOW the root (never the root itself, never a sibling whose
        // name merely starts with the root's — the trailing separator forecloses the "root-evil" confusion).
        if (!fullDest.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            return false;

        nativePath = fullDest;
        return true;
    }

    /// <summary>
    /// Throwing form of <see cref="TryResolveReleaseChild"/> for write sites: returns the safe native path, or
    /// throws <see cref="IOException"/> when the ROM relative path is empty, escaping, or otherwise unsafe. The
    /// existing per-target try/catch in staging/promotion turns the throw into a conservative "target not
    /// secured / release not finalized / incoming retained" outcome — no external write occurs.
    /// </summary>
    public static string ResolveReleaseChild(string releaseRoot, string romName)
    {
        if (!TryResolveReleaseChild(releaseRoot, romName, out var path))
            throw new IOException($"Unsafe or invalid ROM relative path: \"{romName}\"");
        return path;
    }

    /// <summary>
    /// Recursively removes empty directories under <paramref name="root"/>, deepest-first, and (when
    /// <paramref name="removeRoot"/> is true) <paramref name="root"/> itself once it is empty. This drains the
    /// empty nested branches left behind after nested <c>rom.name</c> files are promoted staging → source.
    ///
    /// SAFETY: it NEVER deletes a file and NEVER uses a recursive delete. A directory is removed only when it
    /// contains no files and no surviving subdirectories, so any branch that still holds a file — and every
    /// ancestor of that file — is preserved intact. Best-effort and non-throwing: a directory that cannot be
    /// removed (lock/permission/race) simply stays.
    /// </summary>
    public static void RemoveEmptyDirectoriesBottomUp(string root, bool removeRoot = true)
    {
        try
        {
            if (!Directory.Exists(root)) return;

            // Deepest-first: fully collapse each child subtree before deciding about this level. Materialize the
            // child list so deletions during recursion don't disturb enumeration.
            foreach (var child in Directory.EnumerateDirectories(root).ToList())
                RemoveEmptyDirectoriesBottomUp(child, removeRoot: true);

            // Remove this directory only if nothing (no file, no surviving subdir) remains below it.
            if (removeRoot && !Directory.EnumerateFileSystemEntries(root).Any())
                try { Directory.Delete(root); } catch { /* best-effort */ }
        }
        catch { /* enumeration failed (race/permission) — leave the tree as-is */ }
    }

    /// <summary>
    /// Returns a path inside <paramref name="dir"/> for <paramref name="fileName"/>
    /// that does not collide with an existing file (appends " (1)", " (2)", …).
    /// Never overwrites.
    /// </summary>
    public static string CollisionSafePath(string dir, string fileName)
    {
        var dest = Path.Combine(dir, fileName);
        if (!File.Exists(dest)) return dest;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext            = Path.GetExtension(fileName);
        int counter        = 1;
        while (true)
        {
            dest = Path.Combine(dir, $"{nameWithoutExt} ({counter}){ext}");
            if (!File.Exists(dest)) return dest;
            counter++;
        }
    }
}
