using Arkadia.Data;

namespace Arkadia;

/// <summary>
/// View-model row for the InitializeDiskDialog table.
/// </summary>
public sealed class DiscoveredDiskRow
{
    public required DiscoveredDisk Source { get; init; }

    public string Mountpoint    => Source.Mountpoint;
    public string Label         => Source.FileSystemLabel.Length > 0 ? Source.FileSystemLabel : "(no label)";
    public string TotalLabel    => FormatBytes(Source.TotalCapacityBytes);
    public string FreeLabel     => FormatBytes(Source.FreeSpaceBytes);
    public string DriveFormat   => Source.DriveFormat.Length > 0 ? Source.DriveFormat : "—";
    public string MarkerStatus  => Source.HasMarker ? $"Arkadia: {Source.DiskLabel}" : "—";

    private static string FormatBytes(long b)
    {
        if (b >= 1L << 40) return $"{b / (double)(1L << 40):F1} TB";
        if (b >= 1L << 30) return $"{b / (double)(1L << 30):F1} GB";
        if (b >= 1L << 20) return $"{b / (double)(1L << 20):F1} MB";
        return $"{b / (double)(1L << 10):F0} KB";
    }
}
