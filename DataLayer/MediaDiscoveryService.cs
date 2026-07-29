using System.Collections.Generic;
using Arkadia.Library;

namespace Arkadia.Data;

public sealed record GalleryItem(string Path, bool IsVideo, string Label);
public sealed record CoverItem(string Path, string Label);
public sealed record ExtrasItem(string Path, string Label);

/// <summary>
/// Discovers media files for a library entry and returns typed item lists.
/// Pure data — no UI, no Avalonia dependency, no image loading.
/// </summary>
public sealed class MediaDiscoveryService(string dataDir)
{
    /// <summary>
    /// Returns gallery items in display order: Videos, Title screenshots,
    /// Gameplay screenshots, Fanart.
    /// </summary>
    public IReadOnlyList<GalleryItem> FindGalleryItems(LibraryEntry entry)
    {
        var items = new List<GalleryItem>();
        foreach (var p in MediaStore.FindVideos(dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name))
            items.Add(new GalleryItem(p, true, "Video"));
        foreach (var p in MediaStore.FindTitleScreenshots(dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name))
            items.Add(new GalleryItem(p, false, "Title"));
        foreach (var p in MediaStore.FindScreenshots(dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name))
            items.Add(new GalleryItem(p, false, "Gameplay"));
        foreach (var p in MediaStore.FindFanart(dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name))
            items.Add(new GalleryItem(p, false, "Fanart"));
        return items;
    }

    /// <summary>
    /// Returns cover items in display order: Front, Back, Spine, Wrap.
    /// All regional variants for each type are included.
    /// </summary>
    public IReadOnlyList<CoverItem> FindCoverItems(LibraryEntry entry)
    {
        var items = new List<CoverItem>();
        (string folder, string label)[] types =
        [
            ("covers-front", "Front"), ("covers-back", "Back"),
            ("covers-spine", "Spine"), ("covers-wrap", "Wrap"),
        ];
        foreach (var (folder, label) in types)
            foreach (var (_, path) in MediaStore.FindAllCoverRegions(
                dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name, folder))
                items.Add(new CoverItem(path, label));
        return items;
    }

    /// <summary>
    /// Returns extras items in display order: Logos (HD first, then standard),
    /// Flyer, Marquee.
    /// </summary>
    public IReadOnlyList<ExtrasItem> FindExtrasItems(LibraryEntry entry)
    {
        var items = new List<ExtrasItem>();
        foreach (var p in MediaStore.FindLogos(dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name))
            items.Add(new ExtrasItem(p, "Logo"));
        var flyer = MediaStore.FindFlyer(dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name);
        if (flyer is not null) items.Add(new ExtrasItem(flyer, "Flyer"));
        var marquee = MediaStore.FindMarquee(dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name);
        if (marquee is not null) items.Add(new ExtrasItem(marquee, "Marquee"));
        return items;
    }

    /// <summary>Returns manual file paths, sorted alphabetically.</summary>
    public IReadOnlyList<string> FindManualPaths(LibraryEntry entry)
        => MediaStore.FindManuals(dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name);
}
