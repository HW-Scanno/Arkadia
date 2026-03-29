using System;
using System.IO;
using System.Threading.Tasks;
using Arkadia.Startup;
using Arkadia.Themes;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Arkadia;

public partial class App : Application
{
    /// <summary>Duration of MainWindow's fade-in after the splash screen closes.</summary>
    public static readonly TimeSpan MainFadeInDuration = TimeSpan.FromMilliseconds(200);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyVisualPalette();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (splashPath, soundPath) = StartupBootstrap.Resolve();
            StartupSoundPlayer.TryPlay(soundPath);

            var main = new MainWindow();

            if (splashPath is not null)
            {
                var splash = new SplashWindow(splashPath);
                if (splash.HasImage)
                {
                    // Do NOT set desktop.MainWindow yet — the framework calls
                    // desktop.MainWindow?.Show() after this method returns; null is a safe no-op.
                    // OnLastWindowClose keeps the app alive while the splash is the only window.
                    desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                    _ = RunSplashThenShowMain(splash, main, desktop);
                    base.OnFrameworkInitializationCompleted();
                    return;
                }
            }

            // No splash: show MainWindow normally via the framework
            desktop.MainWindow = main;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Runs the complete splash visual lifecycle, then shows MainWindow while the splash
    /// window is still technically open, switches shutdown mode, then closes the splash.
    /// This ordering guarantees there is never a zero-open-window moment that would trigger
    /// an OnLastWindowClose shutdown before MainWindow is ready.
    /// </summary>
    private static async Task RunSplashThenShowMain(
        SplashWindow splash,
        MainWindow main,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            // Phase 1: splash visual lifecycle — fade-in, min duration, fade-out.
            // Splash window remains open (not closed) after RunAsync returns.
            await splash.RunAsync();

            // Phase 2: show MainWindow while splash is still open.
            // Two windows exist for this brief moment — no zero-window gap possible.
            desktop.MainWindow = main;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Opacity = 0;
            main.Show();

            // Phase 3: close splash now that MainWindow is the live window.
            try { splash.Close(); } catch { }

            // Phase 4: fade MainWindow in
            await FadeInWindowAsync(main);
        }
        catch
        {
            // Degrade safely — always ensure MainWindow ends up visible
            try
            {
                try { splash.Close(); } catch { }
                if (desktop.MainWindow is null)
                {
                    desktop.MainWindow = main;
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                }
                main.Opacity = 1;
                if (!main.IsVisible)
                    main.Show();
            }
            catch { }
        }
    }

    private static async Task FadeInWindowAsync(Window window)
    {
        window.Opacity = 0;
        try
        {
            var animation = new Animation
            {
                Duration  = MainFadeInDuration,
                FillMode  = FillMode.None,
                Children  =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0.0) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
                },
            };
            await animation.RunAsync(window);
        }
        catch { }
        window.Opacity = 1;
    }

    /// <summary>
    /// Resolves the active visual theme's palette and injects entries into Application resources,
    /// overriding the fallback colors defined in App.axaml.
    /// Silently no-ops on any failure — startup must never crash due to palette issues.
    /// </summary>
    private void ApplyVisualPalette()
    {
        try
        {
            var settings = ThemeSettings.Load(
                Path.Combine(AppContext.BaseDirectory, "appconfig.json"));

            var manager = new ThemeManager(
                Path.Combine(AppContext.BaseDirectory, "themes", "visual"),
                settings.ActiveVisualThemeId);

            manager.Scan();

            var theme = manager.ResolveActiveTheme();
            if (theme is null)
                return;

            foreach (var (paletteKey, color) in theme.Palette)
            {
                var resourceKey = PaletteKeyToResourceKey(paletteKey);
                if (resourceKey is not null)
                    Resources[resourceKey] = new SolidColorBrush(color);
            }
        }
        catch
        {
            // Palette application is best-effort — fallback colors remain in effect
        }
    }

    private static string? PaletteKeyToResourceKey(string paletteKey) => paletteKey switch
    {
        "background"    => "ArkBackground",
        "surface"       => "ArkSurface",
        "surfaceAlt"    => "ArkSurfaceAlt",
        "accent"        => "ArkAccent",
        "textPrimary"   => "ArkTextPrimary",
        "textSecondary" => "ArkTextSecondary",
        _               => null,
    };
}
