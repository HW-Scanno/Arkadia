using Arkadia.Disks;
using Xunit;

namespace Arkadia.Tests.Disks;

public sealed class DiskUsageMathTests
{
    // ── Representative values from the original bug report ────────────────────
    // Display: 459.81 GB used / 1863.00 GB capacity (binary GiB labels via FormatBytes).
    // Byte equivalents: 459.81 × 1024³ and 1863 × 1024³.
    // Expected ratio: 459.81 / 1863 ≈ 0.2468

    [Fact]
    public void TypicalDisk_459gb_Of_1863gb_IsAbout24Percent()
    {
        const long gib           = 1024L * 1024 * 1024;
        long usedBytes           = (long)(459.81 * gib);
        long capacityBytes       = (long)(1863.00 * gib);
        double ratio             = DiskUsageMath.CalculateUsageRatio(usedBytes, capacityBytes);

        // 459.81 / 1863 ≈ 0.2468; allow ±0.001 for floating-point rounding
        Assert.InRange(ratio, 0.246, 0.248);
    }

    // ── Edge: zero used ───────────────────────────────────────────────────────

    [Fact]
    public void ZeroUsed_ReturnsZero()
    {
        double ratio = DiskUsageMath.CalculateUsageRatio(usedBytes: 0, capacityBytes: 1_000_000_000L);
        Assert.Equal(0.0, ratio);
    }

    // ── Edge: used == capacity ────────────────────────────────────────────────

    [Fact]
    public void UsedEqualsCapacity_ReturnsOne()
    {
        const long cap  = 2_000_000_000_000L;
        double ratio    = DiskUsageMath.CalculateUsageRatio(usedBytes: cap, capacityBytes: cap);
        Assert.Equal(1.0, ratio);
    }

    // ── Edge: used > capacity (over-allocated) ────────────────────────────────

    [Fact]
    public void UsedExceedsCapacity_ClampsToOne()
    {
        const long cap  = 1_000_000_000L;
        double ratio    = DiskUsageMath.CalculateUsageRatio(usedBytes: cap + 1, capacityBytes: cap);
        Assert.Equal(1.0, ratio);
    }

    // ── Edge: zero capacity ───────────────────────────────────────────────────

    [Fact]
    public void ZeroCapacity_ReturnsZero()
    {
        double ratio = DiskUsageMath.CalculateUsageRatio(usedBytes: 500_000_000L, capacityBytes: 0);
        Assert.Equal(0.0, ratio);
    }
}
