using System;
using Arkadia.Startup;
using Xunit;

namespace Arkadia.Tests.Startup;

/// <summary>
/// Verifies that splash timing constants meet the spec.
/// <para>
/// Visual tests (actual fade-in/out rendering and minimum-duration enforcement)
/// require a live Avalonia UI thread and are verified manually:
/// - Splash fades in smoothly over ~300 ms
/// - Splash remains visible for at least 3 seconds
/// - Splash fades out over ~250 ms then the window closes
/// - If the image is unreadable the window is never shown
/// - Any animation failure snaps to the target opacity and continues
/// </para>
/// </summary>
public sealed class SplashTimingTests
{
    [Fact]
    public void MinVisibleDuration_IsThreeSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(3), SplashWindow.MinVisibleDuration);

    [Fact]
    public void FadeInDuration_IsWithinSpec_250to350ms() =>
        Assert.InRange(SplashWindow.FadeInDuration.TotalMilliseconds, 250, 350);

    [Fact]
    public void FadeOutDuration_IsWithinSpec_200to300ms() =>
        Assert.InRange(SplashWindow.FadeOutDuration.TotalMilliseconds, 200, 300);

    [Fact]
    public void FadeInDuration_IsShorterThanMinVisibleDuration() =>
        Assert.True(SplashWindow.FadeInDuration < SplashWindow.MinVisibleDuration);
}
