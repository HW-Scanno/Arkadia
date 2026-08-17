using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Verify Volume dialog presentation mapping (V-G5 micro-fix): a volume-centric header, a truthful (non
/// read-only) subtitle, and counters drawn from <see cref="VolumeVerifyResult"/>. Pure — no UI harness.
/// </summary>
public sealed class VolumeVerifyPresentationTests
{
    // ── Header: owner shown as Collection (Group) vs DAT Line (Single); never the wrong label ──

    [Fact]
    public void Header_Group_ShowsCollection_NotDatLine()
    {
        var h = VolumeVerifyPresentation.Header("c64-tosec-001", "Commodore 64 TOSEC", isGroup: true);
        Assert.Contains("Volume Verify", h);
        Assert.Contains("c64-tosec-001", h);
        Assert.Contains("Collection: Commodore 64 TOSEC", h);
        Assert.DoesNotContain("DAT Line", h);
        Assert.DoesNotContain("DAT Verify Mode", h);
    }

    [Fact]
    public void Header_Single_ShowsDatLine()
    {
        var h = VolumeVerifyPresentation.Header("ps2-redump-001", "Redump · CD", isGroup: false);
        Assert.Contains("Volume Verify", h);
        Assert.Contains("DAT Line: Redump · CD", h);
        Assert.DoesNotContain("Collection", h);
    }

    [Fact] // no owner description → label only, no dangling separator
    public void Header_NoOwnerDescription_LabelOnly()
        => Assert.Equal("Volume Verify  —  vol-1", VolumeVerifyPresentation.Header("vol-1", "", isGroup: true));

    // ── Subtitle: truthful, never the read-only wording ──

    [Fact]
    public void Subtitle_IsNotReadOnly()
    {
        Assert.NotEqual(VolumeVerifyPresentation.ReadOnlySubtitle, VolumeVerifyPresentation.Subtitle);
        Assert.DoesNotContain("READ-ONLY", VolumeVerifyPresentation.Subtitle);
        Assert.Contains("recovery actions", VolumeVerifyPresentation.Subtitle);
    }

    // ── Counters: mapped from VolumeVerifyResult (labels aligned to values) ──

    [Fact]
    public void Counters_MapFromResult()
    {
        var r = new VolumeVerifyResult
        {
            TotalScanned         = 10,
            Verified             = 6,
            MisplacedRestored    = 2,
            UnwantedMoved        = 1,
            KnownUnexpectedMoved = 1,
            UnknownMoved         = 1,     // MOVED OUT = 3
            StaleAssignmentsRemoved = 1,  // RECONCILED
            Missing              = 1,
            Errors               = 1,
            MisplacedCollisions  = 1,     // ERRORS = 2
        };

        var labels = VolumeVerifyPresentation.CounterLabels;
        var values = VolumeVerifyPresentation.Counters(r);

        Assert.Equal(7, labels.Count);
        Assert.Equal(7, values.Count);
        Assert.Equal("SCANNED",    labels[0]); Assert.Equal(10, values[0]);
        Assert.Equal("RESTORED",   labels[1]); Assert.Equal(2,  values[1]);
        Assert.Equal("MOVED OUT",  labels[2]); Assert.Equal(3,  values[2]);
        Assert.Equal("RECONCILED", labels[3]); Assert.Equal(1,  values[3]);
        Assert.Equal("VERIFIED",   labels[4]); Assert.Equal(6,  values[4]);
        Assert.Equal("MISSING",    labels[5]); Assert.Equal(1,  values[5]);
        Assert.Equal("ERRORS",     labels[6]); Assert.Equal(2,  values[6]);
    }

    [Fact] // the runtime PASS scenario: 2 verified, 1 restored, 0 missing → top counters match completion line
    public void Counters_RuntimePassScenario()
    {
        var r = new VolumeVerifyResult { TotalScanned = 3, Verified = 2, MisplacedRestored = 1, Missing = 0 };
        var v = VolumeVerifyPresentation.Counters(r);
        Assert.Equal(2, v[4]);   // VERIFIED
        Assert.Equal(1, v[1]);   // RESTORED
        Assert.Equal(0, v[5]);   // MISSING
    }
}
