using System;
using System.IO;
using System.Threading.Tasks;
using Arkadia.Startup;
using Arkadia.Themes;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Arkadia;

public partial class App : Application
{
    /// <summary>Duration of MainWindow content fade-in after the splash screen closes.</summary>
    public static readonly TimeSpan MainFadeInDuration = TimeSpan.FromMilliseconds(400);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Capture the last-focused TextBox before the ContextMenu steals focus,
        // so Cut/Copy/Paste commands always target the correct TextBox instance.
        InputElement.GotFocusEvent.AddClassHandler<TextBox>(
            (tb, _) => TextBoxCommands.LastFocused = tb);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyVisualPalette();
        EnsureIncomingFolders();

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
    /// Runs the complete splash visual lifecycle, then shows MainWindow (content hidden)
    /// while the splash window is still technically open, switches shutdown mode,
    /// closes the splash, then fades MainWindow content in.
    ///
    /// The overlap in phases 2-3 prevents a zero-open-window gap that would trigger
    /// OnLastWindowClose. Both windows stay fully opaque to the OS throughout — only
    /// their root content panels have opacity animated, which avoids DWM flicker.
    /// </summary>
    private static async Task RunSplashThenShowMain(
        SplashWindow splash,
        MainWindow main,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            // Phase 1: full splash visual sequence; splash window remains open after return
            await splash.RunAsync();

            // Phase 2: show MainWindow (content invisible) while splash still exists
            desktop.MainWindow = main;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.MainRoot.Opacity = 0;
            main.Show();

            // Phase 3: close splash — MainWindow is now the only window
            try { splash.Close(); } catch { }

            // Phase 4: fade MainWindow content in
            await FadeControlAsync(main.MainRoot);
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
                main.MainRoot.Opacity = 1;
                if (!main.IsVisible)
                    main.Show();
            }
            catch { }
        }
    }

    private static async Task FadeControlAsync(Control control)
    {
        control.Opacity = 0;
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
            await animation.RunAsync(control);
        }
        catch { }
        control.Opacity = 1;
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

    private static void EnsureIncomingFolders()
    {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "incoming-dats"));
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "incoming-roms"));
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
