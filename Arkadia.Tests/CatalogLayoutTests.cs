using Xunit;

namespace Arkadia.Tests;

// Baseline: 2560×1080 (ultrawide workspace the layout was calibrated on).
// scale = min(w/2560, h/1080), clamped to [0.70, 1.45].
public sealed class CatalogLayoutTests
{
    // ── Baseline ──────────────────────────────────────────────────────────────

    [Fact]
    public void Baseline2560x1080_ScaleIsExactlyOne()
    {
        Assert.Equal(1.0, MainWindow.ComputeCatalogLayoutScale(2560, 1080));
    }

    // ── Common target resolutions ─────────────────────────────────────────────

    [Fact]
    public void Resolution1920x1080_ScaleIs0_75()
    {
        // min(1920/2560, 1080/1080) = min(0.75, 1.0) = 0.75
        Assert.Equal(0.75, MainWindow.ComputeCatalogLayoutScale(1920, 1080));
    }

    [Fact]
    public void Resolution3440x1440_ScaleIsApprox1_33()
    {
        // min(3440/2560, 1440/1080) = min(1.3437…, 1.333…) = 1.333…
        double scale = MainWindow.ComputeCatalogLayoutScale(3440, 1440);
        Assert.InRange(scale, 1.33, 1.34);
    }

    [Fact]
    public void Resolution3840x2160_ScaleIsMaxClamp()
    {
        // min(3840/2560, 2160/1080) = min(1.5, 2.0) = 1.5, clamped to 1.45
        Assert.Equal(1.45, MainWindow.ComputeCatalogLayoutScale(3840, 2160));
    }

    // ── Min clamp ─────────────────────────────────────────────────────────────

    [Fact]
    public void Resolution1600x900_ScaleIsMinClamp()
    {
        // min(1600/2560, 900/1080) = min(0.625, 0.833) = 0.625, clamped to 0.70
        Assert.Equal(0.70, MainWindow.ComputeCatalogLayoutScale(1600, 900));
    }

    [Fact]
    public void VerySmallWindow_ScaleIsMinClamp()
    {
        Assert.Equal(0.70, MainWindow.ComputeCatalogLayoutScale(800, 600));
    }

    // ── Max clamp ─────────────────────────────────────────────────────────────

    [Fact]
    public void VeryLargeWindow_ScaleIsMaxClamp()
    {
        Assert.Equal(1.45, MainWindow.ComputeCatalogLayoutScale(7680, 4320));
    }

    // ── Invalid / degenerate inputs return 1.0 ───────────────────────────────

    [Theory]
    [InlineData(0,    1080)]
    [InlineData(1920, 0)]
    [InlineData(0,    0)]
    [InlineData(-1,   1080)]
    [InlineData(1920, -1)]
    public void InvalidDimensions_ReturnDefaultScale(double width, double height)
    {
        Assert.Equal(1.0, MainWindow.ComputeCatalogLayoutScale(width, height));
    }

    // ── Clamp boundaries ─────────────────────────────────────────────────────

    [Fact]
    public void ScaleAlwaysWithinClampRange()
    {
        double[][] cases =
        [
            [1024, 768], [1280, 720], [1366, 768],
            [1920, 1080], [2560, 1080], [2560, 1440],
            [3440, 1440], [3840, 2160], [5120, 2160],
        ];
        foreach (var c in cases)
        {
            double scale = MainWindow.ComputeCatalogLayoutScale(c[0], c[1]);
            Assert.InRange(scale, 0.70, 1.45);
        }
    }
}
