using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Pending;

/// <summary>
/// The data half of the Pending view: every <c>pending</c> reconciliation row of every DAT line, enriched with its
/// releases and DAT-line context, in catalog DAT-line order then creation order. UI-free.
///
/// <para>It visits EVERY leaf database (each through the normal <see cref="DatLineStore"/>, one short-lived connection
/// per call), so it is run off the UI thread and only when the Pending view needs it — never on the startup path. The
/// scan touches no shared state: it works on the catalog rows it is given, each store opens its own connections, and
/// the DAT-line label is resolved through the <c>datLineLabel</c> delegate, which must not read anything another
/// thread mutates.</para>
/// </summary>
public static class PendingItemsLoader
{
    /// <summary>Reads the Systems and the DAT lines from the catalog, then scans.</summary>
    public static List<PendingItem> Load(CatalogService catalog, string dataDir, Func<DatLineRecord, string> datLineLabel)
        => Load(catalog.LoadPlatforms(), catalog.LoadDatLines(), dataDir, datLineLabel);

    /// <summary>Scans the leaf databases of the given DAT lines (catalog rows already read by the caller).</summary>
    public static List<PendingItem> Load(
        IReadOnlyList<HardwareFamilyRecord> allPlatforms, IReadOnlyList<DatLineRecord> allDatLines,
        string dataDir, Func<DatLineRecord, string> datLineLabel)
    {
        var result       = new List<PendingItem>();

        foreach (var dl in allDatLines)
        {
            if (dl.DataStorePath.Length == 0) continue;

            var absPath = Path.Combine(dataDir, dl.DataStorePath);
            if (!File.Exists(absPath)) continue;

            var store = new DatLineStore(absPath);
            var rows  = store.LoadPendingReconciliations("pending");
            if (rows.Count == 0) continue;

            var platformName  = allPlatforms.FirstOrDefault(p => p.Id == dl.HardwareFamilyId)?.Name ?? dl.HardwareFamilyId;
            var relById       = store.LoadReleases().ToDictionary(r => r.Id);
            var filesByRelId  = store.LoadAllReleaseFiles();

            foreach (var row in rows)
            {
                relById.TryGetValue(row.NewReleaseId,      out var newRelease);
                relById.TryGetValue(row.OutdatedReleaseId, out var outdatedRelease);

                // Skip orphaned rows: new_release_id must exist in the release set.
                // Orphans can occur if SaveReleases ran between the row being created
                // and it being loaded (e.g. re-import after an earlier update).
                if (newRelease is null) continue;

                filesByRelId.TryGetValue(row.NewReleaseId,      out var newFiles);
                filesByRelId.TryGetValue(row.OutdatedReleaseId, out var outdatedFiles);

                result.Add(new PendingItem
                {
                    ReconId            = row.Id,
                    Reason             = row.Reason,
                    CreatedAtUtc       = row.CreatedAtUtc,
                    ReconStatus        = row.Status,
                    ArtifactId         = row.ArtifactId         ?? "",
                    VolumeId           = row.VolumeId           ?? "",
                    DiskId             = row.DiskId             ?? "",
                    StoredRelativePath = row.StoredRelativePath ?? "",
                    StoredName         = row.StoredName         ?? "",
                    TargetName         = row.TargetName,
                    TargetRelativePath = row.TargetRelativePath ?? "",
                    DatLineId          = dl.Id,
                    DatLineName        = datLineLabel(dl),
                    PlatformId         = dl.HardwareFamilyId,
                    PlatformName       = platformName,
                    NewRelease         = newRelease,
                    NewRomFiles        = newFiles      ?? [],
                    OutdatedRelease    = outdatedRelease,
                    OutdatedRomFiles   = outdatedFiles ?? [],
                });
            }
        }

        return result;
    }
}
