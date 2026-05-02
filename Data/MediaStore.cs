using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arkadia.Data;

/// <summary>
/// Manages the deterministic media folder structure under data/media/&lt;hardwareFamilyId&gt;/&lt;datLineId&gt;/.
///
/// Non-cover naming:  &lt;stem&gt;_&lt;NNN&gt;.&lt;ext&gt;
/// Cover naming:      &lt;stem&gt;_&lt;region&gt;_&lt;NNN&gt;.&lt;ext&gt;
///   e.g. sonic_wor_001.png, sonic_eu_001.png
/// </summary>
public static class MediaStore
{
    private static readonly string[] MediaFolders =
    [
        "covers-front", "covers-back", "covers-spine", "covers-wrap",
        "screenshots-title", "screenshots",
        "fanart", "videos",
        "logos-hd", "logos",
        "manuals", "marquees", "flyers", "metadata",
        "physical", "physical-texture",
    ];

    // Cover region priority for FindCoverFront
    private static readonly string[] CoverRegionPriority = ["wor", "us", "eu", "jp"];

    /// <summary>Extensions accepted as valid raster images.</summary>
    public static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    public static string DatLinePath(string dataDir, string hardwareFamilyId, string datLineId) =>
        Path.Combine(dataDir, "media", hardwareFamilyId, datLineId);

    /// <summary>Creates all standard media folders. Idempotent — safe to call repeatedly.</summary>
    public static void EnsureMediaFolders(string dataDir, string hardwareFamilyId, string datLineId)
    {
        var root = DatLinePath(dataDir, hardwareFamilyId, datLineId);
        foreach (var folder in MediaFolders)
            Directory.CreateDirectory(Path.Combine(root, folder));
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

    // ── Covers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the best available front-cover path, prioritising regions:
    /// wor → us → eu → jp → any (legacy / unknown region).
    /// Cover filename pattern: &lt;stem&gt;_&lt;region&gt;_&lt;NNN&gt;.&lt;ext&gt;
    /// </summary>
    public static string? FindCoverFront(string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
    {
        var dir = Path.Combine(DatLinePath(dataDir, hardwareFamilyId, datLineId), "covers-front");
        if (!Directory.Exists(dir)) return null;
        var stem = ReleaseStem(releaseName);

        foreach (var region in CoverRegionPriority)
        {
            var match = Directory.EnumerateFiles(dir, $"{stem}_{region}_*")
                .FirstOrDefault(p => ImageExtensions.Contains(Path.GetExtension(p)));
            if (match is not null) return match;
        }

        // Fallback: any image with this stem (handles legacy files without region encoding)
        return Directory.EnumerateFiles(dir, stem + "_*")
            .FirstOrDefault(p => ImageExtensions.Contains(Path.GetExtension(p)));
    }

    /// <summary>
    /// Returns all cover files for the given cover subfolder (e.g. "covers-front"),
    /// each paired with its region code parsed from the filename.
    /// Filename pattern: &lt;stem&gt;_&lt;region&gt;_&lt;NNN&gt;.&lt;ext&gt;
    /// </summary>
    public static IReadOnlyList<(string Region, string Path)> FindAllCoverRegions(
        string dataDir, string hardwareFamilyId, string datLineId,
        string releaseName, string coverSubFolder)
    {
        var dir = Path.Combine(DatLinePath(dataDir, hardwareFamilyId, datLineId), coverSubFolder);
        if (!Directory.Exists(dir)) return [];
        var stemPrefix = ReleaseStem(releaseName) + "_";
        return Directory.EnumerateFiles(dir, stemPrefix + "*")
            .Where(p => ImageExtensions.Contains(Path.GetExtension(p)))
            .Select(p => (Region: ParseRegion(p, stemPrefix), Path: p))
            .Where(x => x.Region.Length > 0)
            .OrderBy(x => x.Path)
            .ToList();
    }

    // ── Screenshots ───────────────────────────────────────────────────────────

    /// <summary>Returns all title screenshot paths (screenshots-title/), sorted alphabetically.</summary>
    public static IReadOnlyList<string> FindTitleScreenshots(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
        => FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "screenshots-title");

    /// <summary>Returns all gameplay screenshot paths (screenshots/), sorted alphabetically.</summary>
    public static IReadOnlyList<string> FindScreenshots(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
        => FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "screenshots");

    /// <summary>
    /// Returns all screenshots ordered for display: title (screenshots-title/) first,
    /// then gameplay (screenshots/). Each group is sorted alphabetically.
    /// </summary>
    public static IReadOnlyList<string> FindAllScreenshots(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
    {
        var title    = FindTitleScreenshots(dataDir, hardwareFamilyId, datLineId, releaseName);
        var gameplay = FindScreenshots(dataDir, hardwareFamilyId, datLineId, releaseName);
        if (title.Count == 0) return gameplay;
        if (gameplay.Count == 0) return title;
        return [.. title, .. gameplay];
    }

    // ── Other media ───────────────────────────────────────────────────────────

    /// <summary>Returns all fanart paths, sorted alphabetically.</summary>
    public static IReadOnlyList<string> FindFanart(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
        => FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "fanart");

    /// <summary>Returns all video paths, sorted alphabetically.</summary>
    public static IReadOnlyList<string> FindVideos(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
        => FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "videos");

    /// <summary>
    /// Returns logo paths: HD logos (logos-hd/) first, then standard logos (logos/).
    /// Each group is sorted alphabetically.
    /// </summary>
    public static IReadOnlyList<string> FindLogos(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
    {
        var hd       = FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "logos-hd");
        var standard = FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "logos");
        if (hd.Count == 0) return standard;
        if (standard.Count == 0) return hd;
        return [.. hd, .. standard];
    }

    /// <summary>Returns the first marquee image path, or null if none exists.</summary>
    public static string? FindMarquee(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
        => FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "marquees").FirstOrDefault();

    /// <summary>Returns the first flyer image path, or null if none exists.</summary>
    public static string? FindFlyer(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
        => FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "flyers").FirstOrDefault();

    /// <summary>Returns all manual paths, sorted alphabetically.</summary>
    public static IReadOnlyList<string> FindManuals(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
        => FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "manuals");

    /// <summary>Returns the first physical-texture image path, or null if none exists.</summary>
    public static string? FindFirstPhysicalTexture(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
        => FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "physical-texture").FirstOrDefault();

    /// <summary>Returns the first flat physical-media image path, or null if none exists.</summary>
    public static string? FindFirstPhysical(
        string dataDir, string hardwareFamilyId, string datLineId, string releaseName)
        => FindInFolder(dataDir, hardwareFamilyId, datLineId, releaseName, "physical").FirstOrDefault();

    // ── Indexed path helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the next available indexed stem for non-cover media.
    /// Pattern: &lt;stem&gt;_&lt;NNN&gt; (no region segment).
    /// </summary>
    public static string NextIndexedMediaStem(
        string dataDir, string hardwareFamilyId, string datLineId,
        string releaseName, string subFolder)
    {
        var dir      = Path.Combine(DatLinePath(dataDir, hardwareFamilyId, datLineId), subFolder);
        Directory.CreateDirectory(dir);
        var stem     = ReleaseStem(releaseName);
        var existing = Directory.EnumerateFiles(dir, stem + "_*").Count();
        return Path.Combine(dir, $"{stem}_{existing + 1:D3}");
    }

    /// <summary>Returns the next available indexed path for non-cover media.</summary>
    public static string NextIndexedMediaPath(
        string dataDir, string hardwareFamilyId, string datLineId,
        string releaseName, string subFolder, string extension)
    {
        var stem = NextIndexedMediaStem(dataDir, hardwareFamilyId, datLineId, releaseName, subFolder);
        var ext  = extension.StartsWith('.') ? extension : "." + extension;
        return stem + ext;
    }

    /// <summary>
    /// Returns the next available indexed stem for a regional cover file.
    /// Pattern: &lt;stem&gt;_&lt;region&gt;_&lt;NNN&gt;.
    /// Only files for the same region are counted, so each region indexes independently.
    /// </summary>
    public static string NextIndexedCoverStem(
        string dataDir, string hardwareFamilyId, string datLineId,
        string releaseName, string coverSubFolder, string region)
    {
        var dir      = Path.Combine(DatLinePath(dataDir, hardwareFamilyId, datLineId), coverSubFolder);
        Directory.CreateDirectory(dir);
        var prefix   = $"{ReleaseStem(releaseName)}_{region}_";
        var existing = Directory.EnumerateFiles(dir, prefix + "*").Count();
        return Path.Combine(dir, $"{prefix}{existing + 1:D3}");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static IReadOnlyList<string> FindInFolder(
        string dataDir, string hardwareFamilyId, string datLineId,
        string releaseName, string subFolder)
    {
        var dir = Path.Combine(DatLinePath(dataDir, hardwareFamilyId, datLineId), subFolder);
        if (!Directory.Exists(dir)) return [];
        var prefix = ReleaseStem(releaseName) + "_";
        return Directory.EnumerateFiles(dir, prefix + "*").OrderBy(p => p).ToList();
    }

    /// <summary>
    /// Parses the region segment from a cover filename.
    /// Input pattern after stemPrefix: &lt;region&gt;_&lt;NNN&gt;.&lt;ext&gt;
    /// Returns empty string if the format doesn't match.
    /// </summary>
    private static string ParseRegion(string filePath, string stemPrefix)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (!name.StartsWith(stemPrefix, StringComparison.OrdinalIgnoreCase)) return "";
        var remainder = name[stemPrefix.Length..]; // "<region>_<NNN>"
        var lastUnderscore = remainder.LastIndexOf('_');
        return lastUnderscore > 0 ? remainder[..lastUnderscore] : "";
    }
}
