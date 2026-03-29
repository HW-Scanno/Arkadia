using System;
using Arkadia;
using Arkadia.Startup;
using Xunit;

namespace Arkadia.Tests.Startup;

/// <summary>
/// Verifies that startup timing constants meet the spec.
/// <para>
/// Startup transition sequence (manually verified — requires live Avalonia UI thread):
///
/// Phase 1  — splash visual lifecycle:
///   desktop.MainWindow = null → framework's Show() call is a null-safe no-op.
///   ShutdownMode = OnLastWindowClose keeps the app alive while splash is the only window.
///   splash.Show() → fade-in (~300 ms) → min-duration wait → fade-out (~250 ms).
///   SplashWindow.RunAsync() returns; window is still OPEN (no self-close).
///
/// Phase 2  — overlap: main shown while splash still exists:
///   desktop.MainWindow = main; ShutdownMode = OnMainWindowClose; main.Opacity=0; main.Show().
///   Two windows exist for a brief moment — no zero-window gap that could trigger shutdown.
///
/// Phase 3  — splash closes:
///   splash.Close() called; only MainWindow remains.
///
/// Phase 4  — main fade-in:
///   MainWindow fades in (~200 ms), then Opacity snapped to 1.
///
/// No-splash path: desktop.MainWindow is set normally; framework shows it directly.
/// </para>
/// </summary>
public sealed class SplashTimingTests
{
    // --- splash constants ---

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

    // --- main window fade-in constant ---

    [Fact]
    public void MainFadeInDuration_IsWithinSpec_150to300ms() =>
        Assert.InRange(App.MainFadeInDuration.TotalMilliseconds, 150, 300);

    [Fact]
    public void MainFadeInDuration_IsShorterThanSplashMinVisible() =>
        Assert.True(App.MainFadeInDuration < SplashWindow.MinVisibleDuration);
}
