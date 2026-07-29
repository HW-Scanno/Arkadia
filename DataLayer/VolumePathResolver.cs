using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Arkadia.Data;

/// <summary>
/// Centralizes volume root path resolution: workspace-first, then mounted disk.
/// Used by LocalArchiveVerifyService, MainWindow, and any future service that needs
/// to locate a volume directory on disk.
/// </summary>
public static class VolumePathResolver
{
    /// <summary>
    /// Resolves the physical root directory for a volume.
    /// Checks the workspace folder first (<c>appRoot\volumes\label</c>), then
    /// the matching Arkadia disk mount (if <paramref name="diskId"/> is provided).
    /// Returns null when the volume root directory cannot be found anywhere.
    /// </summary>
    /// <param name="label">Volume label (unsanitized).</param>
    /// <param name="diskId">Arkadia disk ID stored in volume_locations; null for workspace-only volumes.</param>
    /// <param name="appRoot">Application base directory.</param>
    /// <param name="mountedDisks">
    ///   Optional pre-built map of DiskId → Mountpoint from a single <see cref="DiskDiscoveryService.DiscoverAll"/>
    ///   call. When null the method calls DiscoverAll() itself (one disk scan per call).
    /// </param>
    public static string? Resolve(
        string                               label,
        string?                              diskId,
        string                               appRoot,
        IReadOnlyDictionary<string, string>? mountedDisks = null)
    {
        // 1. Workspace path
        var wsPath = Path.Combine(appRoot, "volumes", SafeLabel(label));
        if (Directory.Exists(wsPath)) return wsPath;

        // 2. Mounted disk path
        if (diskId is not null)
        {
            string? mountpoint = null;

            if (mountedDisks is not null)
                mountedDisks.TryGetValue(diskId, out mountpoint);
            else
            {
                // Discover on demand (caller did not pre-build the map).
                foreach (var d in DiskDiscoveryService.DiscoverAll())
                {
                    if (string.Equals(d.DiskId, diskId, StringComparison.Ordinal))
                    {
                        mountpoint = d.Mountpoint;
                        break;
                    }
                }
            }

            if (mountpoint is not null)
            {
                var diskPath = Path.Combine(mountpoint, SafeLabel(label));
                if (Directory.Exists(diskPath)) return diskPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Converts a volume label to a safe filesystem folder name.
    /// Replaces characters invalid in file names with underscores.
    /// </summary>
    public static string SafeLabel(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim('_', ' ');
        return s.Length > 0 ? s : "volume";
    }
}
