using System;
using System.IO;
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
