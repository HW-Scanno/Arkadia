using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arkadia.Data;

/// <summary>
/// Manages the deterministic media folder structure under data/media/&lt;platformId&gt;/&lt;datLineId&gt;/.
/// File naming convention: &lt;release_name_lowercase&gt;_&lt;NNN&gt;.&lt;ext&gt;
/// </summary>
public static class MediaStore
{
    private static readonly string[] CoverSubs  = ["front", "back", "spine", "wrap", "box3d"];
    private static readonly string[] TopFolders = ["screenshots", "fanart", "videos", "logos", "manuals", "metadata"];

    /// <summary>Extensions accepted as valid raster cover images.</summary>
    public static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    public static string DatLinePath(string dataDir, string platformId, string datLineId) =>
        Path.Combine(dataDir, "media", platformId, datLineId);

    /// <summary>Creates all standard media subfolders. Idempotent — safe to call repeatedly.</summary>
    public static void EnsureMediaFolders(string dataDir, string platformId, string datLineId)
    {
        var root   = DatLinePath(dataDir, platformId, datLineId);
        var covers = Path.Combine(root, "covers");
        foreach (var sub in CoverSubs)
            Directory.CreateDirectory(Path.Combine(covers, sub));
        foreach (var top in TopFolders)
            Directory.CreateDirectory(Path.Combine(root, top));
    }

    /// <summary>
    /// Returns the filesystem-safe stem for a release name.
    /// Lowercase; spaces and Windows-reserved characters replaced with '_'.
    /// This is the single canonical implementation — all media path construction
    /// must call this method rather than performing ad-hoc string normalization.
    /// </summary>
    public static string ReleaseStem(string releaseName) =>
        string.Concat(releaseName.ToLowerInvariant().Select(c =>
            c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' :
            c == ' ' ? '_' : c));

    /// <summary>
    /// Returns the absolute path of the first matching front-cover image for the release,
    /// or null if none is found.
    /// Pattern: covers/front/&lt;stem&gt;_001.*
    /// </summary>
    public static string? FindCoverFront(string dataDir, string platformId, string datLineId, string releaseName)
    {
        var dir = Path.Combine(DatLinePath(dataDir, platformId, datLineId), "covers", "front");
        if (!Directory.Exists(dir)) return null;
        var stem = ReleaseStem(releaseName) + "_001";
        return Directory.EnumerateFiles(dir, stem + ".*")
            .FirstOrDefault(p => ImageExtensions.Contains(Path.GetExtension(p)));
    }

    /// <summary>
    /// Returns all screenshot paths for the release, sorted alphabetically.
    /// Pattern: screenshots/&lt;stem&gt;_NNN.*
    /// </summary>
    public static IReadOnlyList<string> FindScreenshots(string dataDir, string platformId, string datLineId, string releaseName)
    {
        var dir = Path.Combine(DatLinePath(dataDir, platformId, datLineId), "screenshots");
        if (!Directory.Exists(dir)) return [];
        var stem = ReleaseStem(releaseName) + "_";
        return Directory.EnumerateFiles(dir, stem + "*").OrderBy(p => p).ToList();
    }

    /// <summary>
    /// Returns all video paths for the release, sorted alphabetically.
    /// Pattern: videos/&lt;stem&gt;_NNN.*
    /// </summary>
    public static IReadOnlyList<string> FindVideos(string dataDir, string platformId, string datLineId, string releaseName)
    {
        var dir = Path.Combine(DatLinePath(dataDir, platformId, datLineId), "videos");
        if (!Directory.Exists(dir)) return [];
        var stem = ReleaseStem(releaseName) + "_";
        return Directory.EnumerateFiles(dir, stem + "*").OrderBy(p => p).ToList();
    }

    /// <summary>
    /// Returns the next available indexed path stem (no extension) for a media file.
    /// Scans existing files matching &lt;stem&gt;_NNN.* in <paramref name="subFolder"/>,
    /// then returns the path for the next index (001, 002, …).
    /// </summary>
    public static string NextIndexedMediaStem(
        string dataDir, string platformId, string datLineId,
        string releaseName, string subFolder)
    {
        var dir  = Path.Combine(DatLinePath(dataDir, platformId, datLineId), subFolder);
        Directory.CreateDirectory(dir);
        var stem     = ReleaseStem(releaseName);
        var existing = Directory.EnumerateFiles(dir, stem + "_*").Count();
        var index    = existing + 1;
        return Path.Combine(dir, $"{stem}_{index:D3}");
    }

    /// <summary>
    /// Returns the next available indexed path for a media file with the given extension.
    /// </summary>
    public static string NextIndexedMediaPath(
        string dataDir, string platformId, string datLineId,
        string releaseName, string subFolder, string extension)
    {
        var stem = NextIndexedMediaStem(dataDir, platformId, datLineId, releaseName, subFolder);
        var ext  = extension.StartsWith('.') ? extension : "." + extension;
        return stem + ext;
    }
}
