using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>The read-only Single DAT Details mapping: Disk resolved from persisted catalog location only (no disk
/// discovery), and the row carrying the authoritative actual size.</summary>
public sealed class SingleDatDetailsTests
{
    [Fact] // workspace → LOCAL
    public void Disk_Workspace_IsLocal()
        => Assert.Equal("LOCAL", SingleDatDetails.DiskDisplay("workspace", diskLabel: null));

    [Fact] // disk with a persisted label → that label
    public void Disk_WithLabel_IsLabel()
        => Assert.Equal("HDD-07", SingleDatDetails.DiskDisplay("disk", "HDD-07"));

    [Fact] // disk location but no label resolved → placeholder (never blank)
    public void Disk_MissingLabel_IsPlaceholder()
        => Assert.Equal(SingleDatDetails.UnknownDisk, SingleDatDetails.DiskDisplay("disk", diskLabel: null));

    [Fact] // no current location → em dash
    public void Disk_NoLocation_IsDash()
        => Assert.Equal("—", SingleDatDetails.DiskDisplay(null, null));

    [Fact] // an unknown/other location type also falls back to em dash
    public void Disk_UnknownType_IsDash()
        => Assert.Equal("—", SingleDatDetails.DiskDisplay("source", null));

    [Fact] // the row carries the authoritative ACTUAL size bytes (never planned) and the resolved disk
    public void Row_UsesActualSizeAndResolvedDisk()
    {
        var disk = SingleDatDetails.DiskDisplay("disk", "HDD-09");
        var row  = new SingleDatVolumeRow("ps2-002", Artifacts: 91, SizeBytes: 224_000_000_000, Size: "224.0 GB", Disk: disk);

        Assert.Equal("ps2-002", row.Volume);
        Assert.Equal(91, row.Artifacts);
        Assert.Equal(224_000_000_000, row.SizeBytes);   // actual bytes retained
        Assert.Equal("HDD-09", row.Disk);
    }
}
