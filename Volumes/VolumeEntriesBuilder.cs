using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// The data half of the Volumes view: one <see cref="VolumeEntry"/> per catalog volume, in catalog order. UI-free and
/// catalog-only — no leaf database is opened and no file is touched.
///
/// <para>The artifact count of each volume comes from ONE aggregate query
/// (<see cref="CatalogService.GetArtifactCountsByVolume"/>: <c>COUNT(*) … GROUP BY volume_id</c>, every assignment row
/// regardless of its status) instead of materializing every <c>volume_artifacts</c> record of every volume only to
/// count them. Both count exactly the rows whose <c>volume_id</c> equals the volume's id; a volume with no assignment
/// has no aggregate row and shows 0.</para>
/// </summary>
public static class VolumeEntriesBuilder
{
    /// <param name="locationTypeDisplay">Maps a location-type token to its user-facing label (presentation only).</param>
    public static List<VolumeEntry> Build(CatalogService catalog, string dataDir, Func<string, string> locationTypeDisplay)
    {
        var disks    = catalog.GetDisks().ToDictionary(d => d.Id, d => d.Label, StringComparer.Ordinal);
        var datLines = catalog.LoadDatLines().ToDictionary(dl => dl.Id, dl => dl, StringComparer.Ordinal);
        // Group display names, resolved once (Group-owned volumes have no single leaf DB).
        var groupNames = catalog.LoadDatGroups().ToDictionary(g => g.Id.Value, g => g.DisplayName, StringComparer.OrdinalIgnoreCase);
        // Canonical authority/media-type display names for the COLLECTION column (Single owners). Presentation only.
        var authorityNames = catalog.LoadAuthorities().ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);
        var mediaTypeNames = catalog.GetMediaTypes().ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase);
        // Assigned-artifact count per volume, one aggregate query for all volumes (absent = no assignment = 0).
        var artifactCounts = catalog.GetArtifactCountsByVolume();

        return catalog.GetVolumes().Select(v =>
        {
            var loc = catalog.GetCurrentLocation(v.Id);
            string locLabel = loc is null ? "—"
                : loc.LocationType == "disk" && loc.DiskId is not null && disks.TryGetValue(loc.DiskId, out var dl)
                    ? $"disk: {dl}"
                : locationTypeDisplay(loc.LocationType);

            // Ownership is a sum type: Single (dat_line_id) resolves a single leaf DB path; Group (group_id)
            // has no single DB (DbPath stays empty) and displays the Group name.
            DatLineRecord? dlRecord = null;
            if (v.DatLineId is not null) datLines.TryGetValue(v.DatLineId, out dlRecord);
            var dbPath = dlRecord?.DataStorePath.Length > 0
                ? Path.Combine(dataDir, dlRecord.DataStorePath)
                : "";
            // COLLECTION label (presentation only — identity is RawDatLineId / GroupId). Single owners render
            // "<Authority> · <Media>" from canonical names; Group owners render the Group display name.
            var collectionLabel = v.GroupId is not null
                ? VolumeCollectionLabel.ForGroup(
                    groupNames.TryGetValue(v.GroupId, out var gn) ? gn : null, v.GroupId)
                : VolumeCollectionLabel.ForSingle(
                    dlRecord is not null && authorityNames.TryGetValue(dlRecord.Authority, out var an) ? an : null,
                    dlRecord is not null && mediaTypeNames.TryGetValue(dlRecord.MediaTypeId, out var mn) ? mn : null,
                    dlRecord?.Name, v.DatLineId ?? "",
                    dlRecord?.DisplayName);

            return new VolumeEntry
            {
                Id               = v.Id,
                Label            = v.Label,
                PlatformId       = v.PlatformId,
                CollectionLabel  = collectionLabel,
                RawDatLineId     = v.DatLineId,
                GroupId          = v.GroupId,
                DbPath           = dbPath,
                Status           = v.Status,
                Health           = v.Health,
                PlannedSizeBytes = v.PlannedSizeBytes,
                ActualSizeBytes  = v.ActualSizeBytes,
                CurrentLocation  = locLabel,
                DiskId           = loc?.DiskId,
                DiskLabel        = loc?.DiskId is not null && disks.TryGetValue(loc.DiskId, out var diskName)
                    ? diskName : null,
                ArtifactCount    = artifactCounts.TryGetValue(v.Id, out var count) ? count : 0,
            };
        }).ToList();
    }
}
