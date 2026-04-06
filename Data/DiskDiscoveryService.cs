using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Arkadia.Data;

/// <summary>
/// Read-only runtime disk discovery. Enumerates mounted volumes and detects
/// ARKADIA.DISK.json markers. No database access; no state stored.
/// </summary>
public static class DiskDiscoveryService
{
    private const string MarkerFileName = "ARKADIA.DISK.json";

    /// <summary>
    /// Enumerates all fixed/removable drives that are ready, returning a
    /// DiscoveredDisk for each. If a drive carries a valid ARKADIA.DISK.json
    /// the marker fields are populated; otherwise DiskId/DiskLabel are empty.
    /// </summary>
    public static List<DiscoveredDisk> DiscoverAll()
    {
        var result = new List<DiscoveredDisk>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            if (drive.DriveType != DriveType.Fixed &&
                drive.DriveType != DriveType.Removable) continue;

            var mountpoint  = drive.RootDirectory.FullName;
            var fsLabel     = drive.VolumeLabel  ?? "";
            var driveFormat = drive.DriveFormat  ?? "";
            long total      = drive.TotalSize;
            long free       = drive.AvailableFreeSpace;

            var markerPath = Path.Combine(mountpoint, MarkerFileName);
            if (File.Exists(markerPath))
            {
                var marker = TryReadMarker(markerPath);
                if (marker is not null)
                {
                    result.Add(new DiscoveredDisk
                    {
                        Mountpoint         = mountpoint,
                        DiskId             = marker.Value.DiskId,
                        DiskLabel          = marker.Value.DiskLabel,
                        TotalCapacityBytes = total,
                        FreeSpaceBytes     = free,
                        FileSystemLabel    = fsLabel,
                        DriveFormat        = driveFormat,
                        MarkerVersion      = marker.Value.Version,
                        InitializedUtc     = marker.Value.InitializedUtc,
                    });
                    continue;
                }
            }

            result.Add(new DiscoveredDisk
            {
                Mountpoint         = mountpoint,
                TotalCapacityBytes = total,
                FreeSpaceBytes     = free,
                FileSystemLabel    = fsLabel,
                DriveFormat        = driveFormat,
                DiskLabel          = fsLabel,
            });
        }

        return result;
    }

    /// <summary>
    /// Returns the marker file path for a given mountpoint.
    /// </summary>
    public static string MarkerPath(string mountpoint)
        => Path.Combine(mountpoint, MarkerFileName);

    // ── private ──────────────────────────────────────────────────────────────

    private readonly struct MarkerData
    {
        public required string DiskId        { get; init; }
        public required string DiskLabel     { get; init; }
        public required int    Version       { get; init; }
        public required string InitializedUtc { get; init; }
    }

    private static MarkerData? TryReadMarker(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("marker_type", out var mt) ||
                mt.GetString() != "arkadia_disk")
                return null;

            var diskId    = root.TryGetProperty("disk_id",        out var p) ? p.GetString() ?? "" : "";
            var diskLabel = root.TryGetProperty("disk_label",     out var l) ? l.GetString() ?? "" : "";
            var version   = root.TryGetProperty("marker_version", out var v) ? v.GetInt32() : 0;
            var initUtc   = root.TryGetProperty("initialized_utc", out var u) ? u.GetString() ?? "" : "";

            if (diskId.Length == 0) return null;

            return new MarkerData
            {
                DiskId        = diskId,
                DiskLabel     = diskLabel,
                Version       = version,
                InitializedUtc = initUtc,
            };
        }
        catch
        {
            return null;
        }
    }
}
