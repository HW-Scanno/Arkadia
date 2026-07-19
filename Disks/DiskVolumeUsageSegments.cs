using System;
using System.Collections.Generic;

namespace Arkadia.Disks;

/// <summary>Kind of a Disk Details usage-bar segment.</summary>
internal enum DiskUsageSegmentKind { Volume, Untracked, Free }

/// <summary>
/// One segment of the Disk Details usage bar. <see cref="Weight"/> is a fraction of disk
/// capacity — the volume's OWN size / capacity, never a cumulative value — so the caller
/// multiplies it by the bar width to get the segment width directly. <see cref="VolumeIndex"/>
/// selects the palette colour for <see cref="DiskUsageSegmentKind.Volume"/> segments
/// (−1 for untracked/free).
/// </summary>
internal readonly record struct DiskUsageSegment(DiskUsageSegmentKind Kind, int VolumeIndex, double Weight);

/// <summary>
/// Pure builder for the Disk Details usage bar.
///
/// Each volume segment's weight is its own size over disk capacity (never cumulative), so
/// equal-sized volumes render as equal widths. Untracked used space (used − Σ volume sizes)
/// and free space (capacity − used) are appended as their own trailing segments, so free
/// space never replaces or compresses the last volume. Weights sum to ≈1.0 (each clamped to
/// [0,1]); there is no integer rounding, so the final segment is not distorted.
/// </summary>
internal static class DiskVolumeUsageSegments
{
    /// <param name="capacityBytes">Disk declared capacity.</param>
    /// <param name="usedBytes">Disk used bytes (tracked volumes + other usage).</param>
    /// <param name="trackedVolumeSizes">
    ///   ActualSizeBytes of each tracked volume, in display order. The returned Volume
    ///   segments carry the same index, so the caller colours bar and legend identically.
    /// </param>
    internal static IReadOnlyList<DiskUsageSegment> Build(
        long capacityBytes, long usedBytes, IReadOnlyList<long> trackedVolumeSizes)
    {
        var result = new List<DiskUsageSegment>();
        if (capacityBytes <= 0) return result;
        double cap = capacityBytes;

        long tracked = 0;
        for (int i = 0; i < trackedVolumeSizes.Count; i++)
        {
            var size = trackedVolumeSizes[i];
            if (size <= 0) continue;
            tracked += size;
            result.Add(new DiskUsageSegment(
                DiskUsageSegmentKind.Volume, i, Math.Clamp(size / cap, 0.0, 1.0)));
        }

        long untracked = Math.Max(0L, usedBytes - tracked);
        if (untracked > 0)
            result.Add(new DiskUsageSegment(
                DiskUsageSegmentKind.Untracked, -1, Math.Clamp(untracked / cap, 0.0, 1.0)));

        // Free = capacity − used (used is clamped to capacity so free never goes negative).
        long free = Math.Max(0L, capacityBytes - Math.Min(usedBytes, capacityBytes));
        if (free > 0)
            result.Add(new DiskUsageSegment(
                DiskUsageSegmentKind.Free, -1, Math.Clamp(free / cap, 0.0, 1.0)));

        return result;
    }
}
