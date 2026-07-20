using System.Collections.Generic;

namespace Arkadia.Disks;

/// <summary>One row of the Disk Details VOLUMES legend, aligned with a usage-bar segment.</summary>
internal readonly record struct DiskLegendRow(
    DiskUsageSegmentKind Kind, string Label, string ColorHex, long SizeBytes);

/// <summary>
/// Builds the Disk Details legend rows in the SAME order and with the SAME colours as the
/// usage bar, so the two can never drift:
///   1. tracked volume rows — index-coloured via <see cref="DiskVolumeColorPalette.HexForIndex"/>;
///   2. an untracked used-space row (dim/neutral) when used space exceeds the tracked volumes;
///   3. a Free Space row (soft-white) when the disk has free capacity.
/// Untracked and Free are kept distinct — never merged.
/// </summary>
internal static class DiskVolumeLegend
{
    internal const string UntrackedLabel = "Other disk usage";
    internal const string FreeSpaceLabel = "Free Space";

    internal static IReadOnlyList<DiskLegendRow> Build(
        IReadOnlyList<(string Label, long SizeBytes)> trackedVolumes,
        long untrackedBytes,
        long freeBytes)
    {
        var rows = new List<DiskLegendRow>();

        for (int i = 0; i < trackedVolumes.Count; i++)
            rows.Add(new DiskLegendRow(
                DiskUsageSegmentKind.Volume,
                trackedVolumes[i].Label,
                DiskVolumeColorPalette.HexForIndex(i),
                trackedVolumes[i].SizeBytes));

        if (untrackedBytes > 0)
            rows.Add(new DiskLegendRow(
                DiskUsageSegmentKind.Untracked, UntrackedLabel,
                DiskVolumeColorPalette.UntrackedHex, untrackedBytes));

        if (freeBytes > 0)
            rows.Add(new DiskLegendRow(
                DiskUsageSegmentKind.Free, FreeSpaceLabel,
                DiskVolumeColorPalette.FreeSpaceHex, freeBytes));

        return rows;
    }
}
