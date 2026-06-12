using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Unit tests for VolumeEntry computed properties, specifically the FillRatio
/// that drives usage bars in the Volumes view.
/// </summary>
public sealed class VolumeEntryTests
{
    private static VolumeEntry Make(long planned, long actual) => new()
    {
        Id               = "v1",
        Label            = "Test",
        PlatformId       = "snes",
        DatLineId        = "DAT Line",
        RawDatLineId     = "dl1",
        DbPath           = "",
        Status           = "present",
        Health           = "ok",
        PlannedSizeBytes = planned,
        ActualSizeBytes  = actual,
        CurrentLocation  = "workspace",
        DiskId           = null,
        DiskLabel        = null,
    };

    // ── 30. VolumeEntry_FillRatio_IsZeroForEmptyVolume ────────────────────────

    [Fact]
    public void VolumeEntry_FillRatio_IsZeroForEmptyVolume()
    {
        var entry = Make(planned: 1_000_000, actual: 0);
        Assert.Equal(0.0, entry.FillRatio);
    }

    // ── 31. VolumeEntry_FillRatio_IsProportionalToActualBytes ─────────────────

    [Fact]
    public void VolumeEntry_FillRatio_IsProportionalToActualBytes()
    {
        var entry = Make(planned: 1_000, actual: 500);
        Assert.Equal(0.5, entry.FillRatio);
    }

    // ── 32. VolumeEntry_FillRatio_ClampedAtOneWhenOverfilled ──────────────────

    [Fact]
    public void VolumeEntry_FillRatio_ClampedAtOneWhenOverfilled()
    {
        var entry = Make(planned: 1_000, actual: 1_200);
        Assert.Equal(1.0, entry.FillRatio);
    }
}
