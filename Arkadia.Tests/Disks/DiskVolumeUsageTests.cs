using System.Collections.Generic;
using System.Linq;
using Arkadia.Disks;
using Xunit;

namespace Arkadia.Tests.Disks;

/// <summary>
/// Disk Details usage-bar colour + segment policy. Pure helpers (no rendering): assert
/// distinct per-volume colours, correct proportional widths, and free-space behaviour.
/// </summary>
public sealed class DiskVolumeUsageTests
{
    // ── palette ────────────────────────────────────────────────────────────────

    [Fact]
    public void DiskVolumeColorPalette_AssignsDistinctColorsForFirstNVolumes()
    {
        // The whole palette length: every index maps to a unique colour (no collisions).
        var n = DiskVolumeColorPalette.Colors.Length;
        var colors = Enumerable.Range(0, n).Select(DiskVolumeColorPalette.HexForIndex).ToList();
        Assert.Equal(n, colors.Distinct().Count());
    }

    [Fact]
    public void DiskVolumeColorPalette_FourVolumes_AreAllDistinct()
    {
        var four = Enumerable.Range(0, 4).Select(DiskVolumeColorPalette.HexForIndex).ToList();
        Assert.Equal(4, four.Distinct().Count());
    }

    [Fact]
    public void DiskVolumeColorPalette_DoesNotUseBlackOrWhite()
    {
        foreach (var hex in DiskVolumeColorPalette.Colors)
        {
            var up = hex.ToUpperInvariant();
            Assert.NotEqual("#000000", up);
            Assert.NotEqual("#FFFFFF", up);
            // Also reject near-black (all channels < 0x20) so nothing hides on the dark UI.
            var (r, g, b) = Parse(up);
            Assert.False(r < 0x20 && g < 0x20 && b < 0x20, $"{hex} is too close to black");
        }
    }

    [Fact]
    public void DiskVolumeColorPalette_CyclesAfterExhaustion()
    {
        var n = DiskVolumeColorPalette.Colors.Length;
        Assert.Equal(DiskVolumeColorPalette.HexForIndex(0), DiskVolumeColorPalette.HexForIndex(n));
        Assert.Equal(DiskVolumeColorPalette.HexForIndex(1), DiskVolumeColorPalette.HexForIndex(n + 1));
    }

    // ── segments ───────────────────────────────────────────────────────────────

    // The reported scenario: 1863 GB disk, four ~460 GB volumes, ~23 GB free.
    private const long GB  = 1024L * 1024 * 1024;
    private static readonly long Cap  = 1863 * GB;
    private static readonly long V    = 460 * GB;
    private static readonly long Used = 4 * V;   // 1840 GB tracked ≈ used

    [Fact]
    public void DiskVolumeUsageSegments_FourEqualVolumes_AreNearlyEqualWidth()
    {
        var segs = DiskVolumeUsageSegments.Build(Cap, Used, new[] { V, V, V, V });
        var vols = segs.Where(s => s.Kind == DiskUsageSegmentKind.Volume).ToList();

        Assert.Equal(4, vols.Count);
        var min = vols.Min(s => s.Weight);
        var max = vols.Max(s => s.Weight);
        Assert.True(max - min < 0.001, $"volume weights not near-equal: [{min}, {max}]");
        // Each ≈ 460/1863 ≈ 0.247 — none is a sliver.
        Assert.All(vols, s => Assert.InRange(s.Weight, 0.24, 0.25));
    }

    [Fact]
    public void DiskVolumeUsageSegments_FreeSpace_DoesNotReplaceLastVolume()
    {
        var segs = DiskVolumeUsageSegments.Build(Cap, Used, new[] { V, V, V, V });

        // The last VOLUME segment keeps its full proportional weight …
        var lastVolume = segs.Last(s => s.Kind == DiskUsageSegmentKind.Volume);
        Assert.InRange(lastVolume.Weight, 0.24, 0.25);

        // … and free space is a separate, final, small segment.
        Assert.Equal(DiskUsageSegmentKind.Free, segs[^1].Kind);
        var free = segs[^1];
        Assert.InRange(free.Weight, 0.005, 0.02);   // ≈ 23/1863 ≈ 0.012
        Assert.True(free.Weight < lastVolume.Weight);
    }

    [Fact]
    public void DiskVolumeUsageSegments_TotalEqualsDiskCapacityOrClampsSafely()
    {
        var segs = DiskVolumeUsageSegments.Build(Cap, Used, new[] { V, V, V, V });
        Assert.Equal(1.0, segs.Sum(s => s.Weight), 3);   // volumes + free ≈ 1.0
    }

    [Fact]
    public void DiskVolumeUsageSegments_LegendAndBarShareColorMapping()
    {
        // The Volume segment's index is the same index the legend uses → identical colour.
        var segs = DiskVolumeUsageSegments.Build(Cap, Used, new[] { V, V, V, V });
        var vols = segs.Where(s => s.Kind == DiskUsageSegmentKind.Volume).ToList();

        for (int i = 0; i < vols.Count; i++)
        {
            Assert.Equal(i, vols[i].VolumeIndex);
            Assert.Equal(
                DiskVolumeColorPalette.HexForIndex(i),                       // bar colour
                DiskVolumeColorPalette.HexForIndex(vols[i].VolumeIndex));    // legend colour
        }
    }

    [Fact]
    public void DiskVolumeUsageSegments_UntrackedUsage_AppearsBeforeFree()
    {
        // used exceeds the sum of volume sizes → an untracked segment sits between volumes and free.
        long used = Used + 10 * GB;
        var segs = DiskVolumeUsageSegments.Build(Cap, used, new[] { V, V, V, V });

        Assert.Contains(segs, s => s.Kind == DiskUsageSegmentKind.Untracked);
        var untrackedIdx = segs.ToList().FindIndex(s => s.Kind == DiskUsageSegmentKind.Untracked);
        var freeIdx      = segs.ToList().FindIndex(s => s.Kind == DiskUsageSegmentKind.Free);
        Assert.True(untrackedIdx < freeIdx);
    }

    // ── column weights (proportional Grid bar) ─────────────────────────────────

    [Fact]
    public void DiskUsageBar_UsesOneColumnPerSegment()
    {
        var segs    = DiskVolumeUsageSegments.Build(Cap, Used, new[] { V, V, V, V });
        var weights = DiskVolumeUsageSegments.ToColumnWeights(segs);
        Assert.Equal(segs.Count, weights.Length);      // exactly one column per segment
        Assert.All(weights, w => Assert.True(w > 0));  // no zero/collapsed columns
    }

    [Fact]
    public void DiskUsageBar_EqualVolumeWeightsRemainEqual()
    {
        var segs    = DiskVolumeUsageSegments.Build(Cap, Used, new[] { V, V, V, V });
        var weights = DiskVolumeUsageSegments.ToColumnWeights(segs);
        var volWeights = segs.Select((s, i) => (s, w: weights[i]))
                             .Where(t => t.s.Kind == DiskUsageSegmentKind.Volume)
                             .Select(t => t.w).ToList();
        Assert.Equal(4, volWeights.Count);
        Assert.True(volWeights.Max() - volWeights.Min() < 0.001);   // four near-equal columns
    }

    [Fact]
    public void DiskUsageBar_FreeSegmentIsTrailingAndSmall()
    {
        var segs    = DiskVolumeUsageSegments.Build(Cap, Used, new[] { V, V, V, V });
        var weights = DiskVolumeUsageSegments.ToColumnWeights(segs);

        Assert.Equal(DiskUsageSegmentKind.Free, segs[^1].Kind);     // free is the LAST column
        var freeWeight = weights[^1];
        var volWeights = segs.Select((s, i) => (s, w: weights[i]))
                             .Where(t => t.s.Kind == DiskUsageSegmentKind.Volume)
                             .Select(t => t.w);
        Assert.All(volWeights, vw => Assert.True(freeWeight < vw)); // free smaller than every volume
    }

    [Fact]
    public void DiskUsageBar_DoesNotUseRemainderForLastVolume()
    {
        // The last volume column equals its OWN capacity fraction, NOT "capacity − others".
        var segs    = DiskVolumeUsageSegments.Build(Cap, Used, new[] { V, V, V, V });
        var weights = DiskVolumeUsageSegments.ToColumnWeights(segs);

        var volEntries = segs.Select((s, i) => (s, w: weights[i]))
                             .Where(t => t.s.Kind == DiskUsageSegmentKind.Volume).ToList();
        var lastVolWeight  = volEntries[^1].w;
        var firstVolWeight = volEntries[0].w;
        Assert.True(System.Math.Abs(lastVolWeight - firstVolWeight) < 0.001);   // same as the first
        Assert.InRange(lastVolWeight, 0.24, 0.25);                              // ≈ 460/1863, not a remainder
    }

    [Fact]
    public void DiskVolumeUsageSegments_ZeroCapacity_ReturnsNoSegments()
        => Assert.Empty(DiskVolumeUsageSegments.Build(0, 0, new[] { V }));

    [Fact]
    public void DiskVolumeUsageSegments_FullDisk_NoFreeSegment()
    {
        var segs = DiskVolumeUsageSegments.Build(Cap, Cap, new[] { Cap });
        Assert.DoesNotContain(segs, s => s.Kind == DiskUsageSegmentKind.Free);
    }

    private static (int R, int G, int B) Parse(string hex)
        => (System.Convert.ToInt32(hex.Substring(1, 2), 16),
            System.Convert.ToInt32(hex.Substring(3, 2), 16),
            System.Convert.ToInt32(hex.Substring(5, 2), 16));
}
