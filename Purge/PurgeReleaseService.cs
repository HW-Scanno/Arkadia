using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Purge;

/// <summary>
/// Result of a completed or failed <see cref="PurgeReleaseService.Execute"/> call.
/// </summary>
public sealed class PurgeResult
{
    public bool   Success         { get; init; }
    public int    FilesDeleted    { get; init; }
    public long   LocalBytesFreed  { get; init; }
    public long   VolumeBytesFreed { get; init; }
    public string? ErrorMessage   { get; init; }

    /// <summary>Labels of volumes whose usage was refreshed.</summary>
    public IReadOnlyList<string> RefreshedVolumeLabels { get; init; } = [];
}

/// <summary>
/// Executes a <see cref="PurgeReleasePlan"/> produced by <see cref="PurgeReleasePlanner"/>.
///
/// Safety invariants:
/// — Revalidates the plan before any delete.
/// — Deletes one file at a time, confirming File.Exists == false after each delete.
/// — Does NOT mark the release UNWANTED unless every planned delete succeeded.
/// — Stops and reports on first delete failure.
/// — Never deletes a file not listed in the plan.
/// </summary>
public sealed class PurgeReleaseService
{
    private readonly string         _appRoot;
    private readonly CatalogService _catalog;

    public PurgeReleaseService(string appRoot, CatalogService catalog)
    {
        _appRoot = appRoot;
        _catalog = catalog;
    }

    public PurgeResult Execute(PurgeReleasePlan plan)
    {
        // ── Revalidate ────────────────────────────────────────────────────────
        if (!plan.CanExecute)
            return Fail("Plan is not executable (offline disks or blocking issues).");

        // Re-check that all required disks are still mounted
        var mountedByDiskId = DiskDiscoveryService.DiscoverAll()
            .Where(d => d.DiskId.Length > 0)
            .ToDictionary(d => d.DiskId, d => d.Mountpoint, StringComparer.Ordinal);

        var stillOffline = plan.VolumeArtifacts
            .Where(v => v.DiskId.Length > 0 && !mountedByDiskId.ContainsKey(v.DiskId))
            .Select(v => v.DiskLabel)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (stillOffline.Count > 0)
            return Fail($"Required disks went offline: {string.Join(", ", stillOffline)}");

        int  filesDeleted     = 0;
        long localBytesFreed  = 0;
        long volumeBytesFreed = 0;
        var  refreshedLabels  = new List<string>();

        // ── Delete local archive artifacts ────────────────────────────────────
        foreach (var la in plan.LocalArtifacts)
        {
            if (!la.FileExists) continue;   // already absent — treat as success

            try { File.Delete(la.AbsolutePath); }
            catch (Exception ex)
            {
                return Fail($"Failed to delete local artifact {la.FileName}: {ex.Message}");
            }

            if (File.Exists(la.AbsolutePath))
                return Fail($"Delete reported success but file still exists: {la.AbsolutePath}");

            filesDeleted++;
            localBytesFreed += la.Bytes;
        }

        // ── Delete volume artifacts ───────────────────────────────────────────
        foreach (var va in plan.VolumeArtifacts)
        {
            if (va.AbsolutePath is null)
            {
                // Path could not be resolved earlier but disk is "mounted" (workspace).
                // Skip — if the file doesn't exist it's already gone.
                continue;
            }

            if (File.Exists(va.AbsolutePath))
            {
                try { File.Delete(va.AbsolutePath); }
                catch (Exception ex)
                {
                    return Fail($"Failed to delete volume artifact {va.FileName} " +
                                $"on volume {va.VolumeLabel}: {ex.Message}");
                }

                if (File.Exists(va.AbsolutePath))
                    return Fail($"Delete reported success but file still exists: {va.AbsolutePath}");

                filesDeleted++;
                volumeBytesFreed += va.Bytes;
            }

            // Remove volume_artifact row and decrement volume's actual_size_bytes
            _catalog.DeleteVolumeArtifactRow(va.VolumeArtifactId, va.VolumeId, va.Bytes);

            var vol = _catalog.GetVolumeById(va.VolumeId);
            if (vol is not null && !refreshedLabels.Contains(vol.Label))
                refreshedLabels.Add(vol.Label);
        }

        // ── Update per-DAT-line DB ────────────────────────────────────────────
        if (File.Exists(plan.DbPath))
        {
            var store = new DatLineStore(plan.DbPath);

            // Delete derived artifact rows
            foreach (var la in plan.LocalArtifacts)
                store.DeleteDerivedArtifactRow(la.DerivedArtifactId);

            // Delete release_content_links
            store.DeleteReleaseContentLinks(plan.ReleaseId);

            // Mark release unwanted
            store.UpdateReleaseStatus(plan.ReleaseId, "unwanted");

            // Hide from catalog by default
            store.SetShowInCatalog(plan.ReleaseId, false);
        }

        return new PurgeResult
        {
            Success              = true,
            FilesDeleted         = filesDeleted,
            LocalBytesFreed      = localBytesFreed,
            VolumeBytesFreed     = volumeBytesFreed,
            RefreshedVolumeLabels = refreshedLabels,
        };
    }

    private static PurgeResult Fail(string message) => new()
    {
        Success      = false,
        ErrorMessage = message,
    };
}
