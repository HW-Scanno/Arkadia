using System.Linq;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests;

public class CatalogMediaListHelpersTests
{
    private static ReleaseMediaAsset MakeAsset(string mediaType, string file = "a.png") =>
        new("rel-1", mediaType, $"/fake/{file}", file, 100, null, true, false, false, null, null, null);

    private static MediaAssetVm Vm(string mediaType, string file = "a.png") =>
        new(MakeAsset(mediaType, file));

    // ── BuildGroupedDisplay ───────────────────────────────────────────────────

    [Fact]
    public void BuildGroupedDisplay_Empty_ReturnsEmpty()
    {
        var result = CatalogMediaListHelpers.BuildGroupedDisplay([]);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildGroupedDisplay_SingleType_InsertsOneHeader()
    {
        var vms    = new[] { Vm("cover-front", "a.png"), Vm("cover-front", "b.png") };
        var result = CatalogMediaListHelpers.BuildGroupedDisplay(vms);

        var headers = result.OfType<MediaGroupHeaderVm>().ToList();
        Assert.Single(headers);
    }

    [Fact]
    public void BuildGroupedDisplay_SingleType_TotalCountIsHeaderPlusAssets()
    {
        var vms    = new[] { Vm("cover-front", "a.png"), Vm("cover-front", "b.png"), Vm("cover-front", "c.png") };
        var result = CatalogMediaListHelpers.BuildGroupedDisplay(vms);

        // 1 header + 3 assets
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void BuildGroupedDisplay_TwoTypes_InsertsHeaderPerType()
    {
        var vms = new[]
        {
            Vm("cover-front",  "cf.png"),
            Vm("screenshot",   "sc.png"),
            Vm("screenshot",   "sc2.png"),
        };
        var result  = CatalogMediaListHelpers.BuildGroupedDisplay(vms);
        var headers = result.OfType<MediaGroupHeaderVm>().ToList();

        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void BuildGroupedDisplay_TwoTypes_TotalCountIsHeadersPlusAssets()
    {
        var vms = new[]
        {
            Vm("cover-front", "cf.png"),
            Vm("screenshot",  "sc.png"),
        };
        var result = CatalogMediaListHelpers.BuildGroupedDisplay(vms);

        // 2 headers + 2 assets
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void BuildGroupedDisplay_HeaderLabel_IsUppercasedWithCount()
    {
        var vms    = new[] { Vm("cover-front", "a.png"), Vm("cover-front", "b.png") };
        var result = CatalogMediaListHelpers.BuildGroupedDisplay(vms);

        var header = result.OfType<MediaGroupHeaderVm>().Single();
        Assert.Equal("COVER FRONT · 2", header.Label);
    }

    [Fact]
    public void BuildGroupedDisplay_HeaderCount_MatchesAssetsInGroup()
    {
        var vms = new[]
        {
            Vm("screenshot", "s1.png"),
            Vm("screenshot", "s2.png"),
            Vm("screenshot", "s3.png"),
        };
        var result = CatalogMediaListHelpers.BuildGroupedDisplay(vms);
        var header = result.OfType<MediaGroupHeaderVm>().Single();

        Assert.Equal(3, header.Count);
    }

    [Fact]
    public void BuildGroupedDisplay_AssetsFollowTheirHeader()
    {
        var vms = new[]
        {
            Vm("cover-front", "cf.png"),
            Vm("screenshot",  "sc.png"),
        };
        var result = CatalogMediaListHelpers.BuildGroupedDisplay(vms);

        // Interleaved: header, asset, header, asset
        Assert.IsType<MediaGroupHeaderVm>(result[0]);
        Assert.IsType<MediaAssetVm>(result[1]);
        Assert.IsType<MediaGroupHeaderVm>(result[2]);
        Assert.IsType<MediaAssetVm>(result[3]);
    }

    [Fact]
    public void BuildGroupedDisplay_GroupOrder_FollowsInputOrder()
    {
        var vms = new[]
        {
            Vm("video",       "v.mp4"),
            Vm("cover-front", "cf.png"),
        };
        var result  = CatalogMediaListHelpers.BuildGroupedDisplay(vms);
        var headers = result.OfType<MediaGroupHeaderVm>().ToList();

        Assert.Equal("VIDEO",       headers[0].MediaType.ToUpperInvariant());
        Assert.Equal("COVER-FRONT", headers[1].MediaType.ToUpperInvariant());
    }

    // ── Non-All filter produces a flat list (no headers) ─────────────────────

    [Fact]
    public void FlatDisplay_ContainsNoHeaders()
    {
        // When filter is not All, caller uses Cast<object>() — no headers
        var vms = new[]
        {
            Vm("cover-front", "a.png"),
            Vm("cover-front", "b.png"),
        };
        // Simulate what ApplyTypeFilter does for non-All case
        var display = vms.Cast<object>().ToList();

        Assert.DoesNotContain(display, item => item is MediaGroupHeaderVm);
    }
}
