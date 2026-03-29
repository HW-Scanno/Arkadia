using System;
using Arkadia;
using Arkadia.Startup;
using Xunit;

namespace Arkadia.Tests.Startup;

/// <summary>
/// Verifies that startup timing constants meet the spec.
/// <para>
/// Fade strategy (manually verified — requires live Avalonia UI thread):
///   Window.Opacity is never changed. Only the root content panel of each window
///   (SplashRoot / MainRoot) has its opacity animated. This keeps each window fully
///   opaque to the OS/DWM compositor, which eliminates the flicker caused by
///   window-level transparency changes on Windows.
///
/// Startup sequence:
///   Phase 1  — SplashRoot fades 0→1 over ~600 ms; window always at Opacity=1.
///   Phase 2  — SplashRoot held at 1 for remainder of MinVisibleDuration (~3 s).
///   Phase 3  — SplashRoot fades 1→0 over ~500 ms. RunAsync() returns; splash still open.
///   Phase 4  — MainWindow.Show(); MainRoot.Opacity=0 (content hidden, window opaque).
///              splash.Close() — MainWindow is now the only window.
///   Phase 5  — MainRoot fades 0→1 over ~400 ms. Startup complete.
/// </para>
/// </summary>
public sealed class SplashTimingTests
{
    // --- splash constants ---

    [Fact]
    public void MinVisibleDuration_IsThreeSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(3), SplashWindow.MinVisibleDuration);

    [Fact]
    public void FadeInDuration_IsWithinSpec_500to700ms() =>
        Assert.InRange(SplashWindow.FadeInDuration.TotalMilliseconds, 500, 700);

    [Fact]
    public void FadeOutDuration_IsWithinSpec_400to600ms() =>
        Assert.InRange(SplashWindow.FadeOutDuration.TotalMilliseconds, 400, 600);

    [Fact]
    public void FadeInDuration_IsShorterThanMinVisibleDuration() =>
        Assert.True(SplashWindow.FadeInDuration < SplashWindow.MinVisibleDuration);

    // --- main window fade-in constant ---

    [Fact]
    public void MainFadeInDuration_IsWithinSpec_300to500ms() =>
        Assert.InRange(App.MainFadeInDuration.TotalMilliseconds, 300, 500);

    [Fact]
    public void MainFadeInDuration_IsShorterThanSplashMinVisible() =>
        Assert.True(App.MainFadeInDuration < SplashWindow.MinVisibleDuration);
}
