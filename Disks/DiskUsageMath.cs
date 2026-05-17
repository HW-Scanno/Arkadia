namespace Arkadia.Disks;

internal static class DiskUsageMath
{
    /// <summary>
    /// Returns used/capacity clamped to [0, 1].
    /// Returns 0 when <paramref name="capacityBytes"/> is zero or negative.
    /// </summary>
    internal static double CalculateUsageRatio(long usedBytes, long capacityBytes)
        => capacityBytes > 0
            ? System.Math.Clamp((double)usedBytes / capacityBytes, 0.0, 1.0)
            : 0.0;
}
