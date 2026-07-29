using System;
using System.IO;
using System.Management;
using System.Runtime.Versioning;

namespace Arkadia.Data;

/// <summary>
/// Best-effort Windows WMI lookup of physical disk hardware metadata
/// (manufacturer, model, serial number) for a given mounted drive root path.
/// Never throws — all failures are silently absorbed and empty strings returned.
/// Never used as identity; result is informational only.
/// </summary>
public readonly struct DiskHardwareInfo
{
    public string Manufacturer { get; init; }
    public string Model        { get; init; }
    public string SerialNumber { get; init; }
}

[SupportedOSPlatform("windows")]
public static class DiskHardwareEnricher
{

    /// <summary>
    /// Attempts to resolve hardware info for the drive at <paramref name="mountpoint"/>
    /// (e.g. <c>D:\</c>). Returns empty strings on any failure.
    /// </summary>
    public static DiskHardwareInfo TryGetInfo(string mountpoint)
    {
        try
        {
            // Trim to bare device ID: "D:\" → "D:"
            var deviceId = mountpoint.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (deviceId.Length < 2)
                return default;

            // Logical disk → partition
            var partitionId = string.Empty;
            using (var ldSearch = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{deviceId}'}} " +
                "WHERE AssocClass=Win32_LogicalDiskToPartition " +
                "ResultClass=Win32_DiskPartition"))
            {
                foreach (ManagementObject part in ldSearch.Get())
                {
                    partitionId = part["DeviceID"]?.ToString() ?? "";
                    part.Dispose();
                    break;
                }
            }

            if (partitionId.Length == 0)
                return default;

            // Partition → physical disk drive
            using var diskSearch = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} " +
                "WHERE AssocClass=Win32_DiskDriveToDiskPartition " +
                "ResultClass=Win32_DiskDrive");

            foreach (ManagementObject disk in diskSearch.Get())
            {
                using (disk)
                {
                    var manufacturer = Normalise(disk["Manufacturer"]?.ToString());
                    var model        = Normalise(disk["Model"]?.ToString());
                    var serial       = Normalise(disk["SerialNumber"]?.ToString());

                    // Filter out generic WMI placeholder
                    if (string.Equals(manufacturer, "(Standard disk drives)",
                            StringComparison.OrdinalIgnoreCase))
                        manufacturer = "";

                    return new DiskHardwareInfo
                    {
                        Manufacturer = manufacturer,
                        Model        = model,
                        SerialNumber = serial,
                    };
                }
            }
        }
        catch { /* best-effort — never block the caller */ }

        return default;
    }

    private static string Normalise(string? s) => s?.Trim() ?? "";
}
