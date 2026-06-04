using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Purge;

/// <summary>
/// Builds a <see cref="PurgeReleasePlan"/> without performing any destructive action.
/// All file system and database access is read-only.
/// </summary>
public sealed class PurgeReleasePlanner
{
    private readonly string         _appRoot;
    private readonly CatalogService _catalog;

    public PurgeReleasePlanner(string appRoot, CatalogService catalog)
    {
        _appRoot = appRoot;
        _catalog = catalog;
    }

    /// <summary>
    /// Builds a dry-run plan for purging the given release.
    /// </summary>
    public PurgeReleasePlan Plan(
        string releaseId,
        string releaseName,
        string currentStatus,
        string datLineId,
        string dbPath)
    {
        var warnings = new List<string>();
        var issues   = new List<string>();

        // ── Local archive artifacts ───────────────────────────────────────────
        var localArtifacts = new List<PurgeLocalArtifact>();

        if (File.Exists(dbPath))
        {
            var store   = new DatLineStore(dbPath);
            var derived = store.GetDerivedArtifactsByReleaseId(releaseId);

            foreach (var da in derived)
            {
                var absPath = Path.Combine(
                    _appRoot,
                    da.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var exists = File.Exists(absPath);
                localArtifacts.Add(new PurgeLocalArtifact(
                    da.Id, da.FileName, absPath, da.DerivedSizeBytes, exists));
            }
        }
        else
        {
            issues.Add($"DAT-line database not found: {dbPath}");
        }

        // ── Volume artifacts ──────────────────────────────────────────────────
        var volumeArtifacts = new List<PurgeVolumeArtifact>();
        var allDisks        = _catalog.GetDisks().ToDictionary(d => d.Id, StringComparer.Ordinal);

        // Discover currently-mounted disks
        var mountedByDiskId = DiskDiscoveryService.DiscoverAll()
            .Where(d => d.DiskId.Length > 0)
            .ToDictionary(d => d.DiskId, d => d.Mountpoint, StringComparer.Ordinal);

        foreach (var la in localArtifacts)
        {
            var vaRows = _catalog.GetVolumeArtifactsByDerivedId(la.DerivedArtifactId);
            foreach (var va in vaRows)
            {
                var vol = _catalog.GetVolumeById(va.VolumeId);
                if (vol is null) continue;

                // Resolve volume location to find disk
                var loc    = _catalog.GetCurrentLocation(va.VolumeId);
                var diskId = loc?.DiskId ?? "";
                var diskLabel = diskId.Length > 0 && allDisks.TryGetValue(diskId, out var dk)
                    ? dk.Label : "—";
                var mounted = diskId.Length > 0 && mountedByDiskId.ContainsKey(diskId);

                string? absPath = null;
                if (mounted && diskId.Length > 0 && mountedByDiskId.TryGetValue(diskId, out var mp))
                {
                    var safeLabel = SafeFileName(vol.Label);
                    absPath = Path.Combine(mp, safeLabel, la.FileName);
                }
                else if (loc?.LocationType == "workspace")
                {
                    // Volume lives in local workspace
                    var safeLabel = SafeFileName(vol.Label);
                    var wsPath = Path.Combine(_appRoot, "volumes", safeLabel, la.FileName);
                    if (File.Exists(wsPath))
                        absPath = wsPath;
                }

                volumeArtifacts.Add(new PurgeVolumeArtifact(
                    VolumeArtifactId: va.Id,
                    VolumeId:         va.VolumeId,
                    VolumeLabel:      vol.Label,
                    DerivedArtifactId: la.DerivedArtifactId,
                    DatLineId:        va.DatLineId,
                    FileName:         la.FileName,
                    AbsolutePath:     absPath,
                    DiskId:           diskId,
                    DiskLabel:        diskLabel,
                    DiskMounted:      mounted || loc?.LocationType == "workspace",
                    Bytes:            la.Bytes));
            }
        }

        // ── Required / offline disk analysis ─────────────────────────────────
        var requiredDiskLabels = volumeArtifacts
            .Where(v => v.DiskId.Length > 0)
            .Select(v => v.DiskLabel)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var offlineDiskLabels = volumeArtifacts
            .Where(v => v.DiskId.Length > 0 && !v.DiskMounted)
            .Select(v => v.DiskLabel)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (offlineDiskLabels.Count > 0)
            issues.Add($"Required disks offline: {string.Join(", ", offlineDiskLabels)}");

        foreach (var va in volumeArtifacts.Where(v => v.DiskMounted && v.AbsolutePath is null))
            warnings.Add($"Could not resolve path for volume artifact on volume {va.VolumeLabel}");

        bool canExecute = issues.Count == 0;

        return new PurgeReleasePlan
        {
            ReleaseId      = releaseId,
            ReleaseName    = releaseName,
            CurrentStatus  = currentStatus,
            DatLineId      = datLineId,
            DbPath         = dbPath,
            LocalArtifacts = localArtifacts,
            VolumeArtifacts = volumeArtifacts,
            TotalLocalBytes  = localArtifacts.Sum(a => a.Bytes),
            TotalVolumeBytes = volumeArtifacts.Sum(a => a.Bytes),
            RequiredDiskLabels = requiredDiskLabels,
            OfflineDiskLabels  = offlineDiskLabels,
            Warnings = warnings,
            Issues   = issues,
            CanExecute = canExecute,
        };
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
