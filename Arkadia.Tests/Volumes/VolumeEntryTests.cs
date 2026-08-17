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

    // Move-gating fixture: status/location/ownership drive the Move action's availability predicate.
    private static VolumeEntry MoveCase(string status, string currentLocation, bool group) => new()
    {
        Id               = "v1",
        Label            = "Test",
        PlatformId       = "c64",
        DatLineId        = "DAT Line",
        RawDatLineId     = group ? null : "dl1",
        GroupId          = group ? "c64-tosec" : null,
        DbPath           = "",
        Status           = status,
        Health           = "ok",
        PlannedSizeBytes = 1_000,
        ActualSizeBytes  = 100,
        CurrentLocation  = currentLocation,
        DiskId           = currentLocation.StartsWith("disk:") ? "disk-1" : null,
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

    // ── Move-action gating (V-G4 reachability): owner-agnostic; built (LOCAL/ON DISK) only ────

    [Fact] // Single + LOCAL → Move enabled
    public void CanMoveVolume_Single_Local_Enabled()
        => Assert.True(MoveCase("present", "Local Archive", group: false).CanMoveVolume);

    [Fact] // Single + ON DISK → Move enabled
    public void CanMoveVolume_Single_OnDisk_Enabled()
    {
        var e = MoveCase("present", "disk:MyDisk", group: false);
        Assert.Equal("ON DISK", e.StatusLabel);
        Assert.True(e.CanMoveVolume);
    }

    [Fact] // Group + LOCAL → Move enabled (the V-G4 reachability fix)
    public void CanMoveVolume_Group_Local_Enabled()
    {
        var e = MoveCase("present", "Local Archive", group: true);
        Assert.True(e.IsGroupOwned);
        Assert.Equal("LOCAL", e.StatusLabel);
        Assert.True(e.CanMoveVolume);
    }

    [Fact] // Group + ON DISK → Move enabled
    public void CanMoveVolume_Group_OnDisk_Enabled()
    {
        var e = MoveCase("present", "disk:MyDisk", group: true);
        Assert.True(e.IsGroupOwned);
        Assert.Equal("ON DISK", e.StatusLabel);
        Assert.True(e.CanMoveVolume);
    }

    [Theory] // negative states stay disabled for both ownership kinds
    [InlineData("init", false)]
    [InlineData("init", true)]
    [InlineData("lost", false)]
    [InlineData("lost", true)]
    public void CanMoveVolume_NotBuiltOrLost_Disabled(string status, bool group)
        => Assert.False(MoveCase(status, "—", group).CanMoveVolume);

    // ── Verify-action gating (V-G5): Single truth table unchanged; Group needs no DbPath ────

    private static VolumeEntry VerifyCase(bool group, int artifactCount) => new()
    {
        Id = "v1", Label = "Test", PlatformId = "c64", DatLineId = "DAT Line",
        RawDatLineId = group ? null : "dl1", GroupId = group ? "c64-tosec" : null, DbPath = "",
        Status = "present", Health = "ok", PlannedSizeBytes = 1_000, ActualSizeBytes = 100,
        CurrentLocation = "workspace", DiskId = null, DiskLabel = null,
        ArtifactCount = artifactCount,
    };

    [Theory] // Single: exactly (artifacts > 0) AND (leaf DB available) — the historical truth table, unchanged
    [InlineData(1, true,  true)]
    [InlineData(1, false, false)]   // no DB → disabled (regression guard)
    [InlineData(0, true,  false)]   // no artifacts → disabled
    [InlineData(0, false, false)]
    public void CanVerifyVolume_Single_TruthTableUnchanged(int artifacts, bool dbAvailable, bool expected)
        => Assert.Equal(expected, VerifyCase(group: false, artifacts).CanVerifyVolume(dbAvailable));

    [Theory] // Group: requires only assigned artifacts — never a Single DbPath (validated at execution time)
    [InlineData(1, false, true)]    // artifacts present, no DB probe → enabled
    [InlineData(1, true,  true)]
    [InlineData(0, false, false)]   // no artifacts → disabled
    public void CanVerifyVolume_Group_RequiresArtifactsOnly(int artifacts, bool dbAvailable, bool expected)
        => Assert.Equal(expected, VerifyCase(group: true, artifacts).CanVerifyVolume(dbAvailable));
}
