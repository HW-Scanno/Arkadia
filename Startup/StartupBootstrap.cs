using System;
using System.IO;
using Arkadia.Themes;
using Arkadia.Themes.Assets;

namespace Arkadia.Startup;

/// <summary>
/// Resolves startup assets for the active visual and audio themes.
/// Splash comes from the visual branch; startup sound comes from the audio branch.
/// All file-system access is isolated here; the UI layer only receives paths.
/// </summary>
internal static class StartupBootstrap
{
    /// <summary>Resolves assets using paths relative to the application's base directory.</summary>
    public static (string? splashPath, string? soundPath) Resolve() =>
        Resolve(
            themesRoot: Path.Combine(AppContext.BaseDirectory, "themes"),
            configPath: Path.Combine(AppContext.BaseDirectory, "appconfig.json"),
            statePath:  Path.Combine(AppContext.BaseDirectory, "startup_state.json"));

    /// <summary>
    /// Resolves assets using explicit paths — used directly in tests.
    /// Returns (null, null) on any failure; startup must never crash due to missing assets.
    /// </summary>
    public static (string? splashPath, string? soundPath) Resolve(
        string themesRoot,
        string configPath,
        string statePath)
    {
        try
        {
            var settings = ThemeSettings.Load(configPath);

            var visualManager = new ThemeManager(
                Path.Combine(themesRoot, "visual"), settings.ActiveVisualThemeId);
            var audioManager = new ThemeManager(
                Path.Combine(themesRoot, "audio"), settings.ActiveAudioThemeId);

            visualManager.Scan();
            audioManager.Scan();

            var visualTheme = visualManager.ResolveActiveTheme();
            var audioTheme  = audioManager.ResolveActiveTheme();

            var selector = new ThemeAssetSelector(statePath);
            return (
                visualTheme is null ? null : selector.GetNextImage(visualTheme, ThemeFamily.Image.Splash),
                audioTheme  is null ? null : selector.GetNextSound(audioTheme,  ThemeFamily.Sound.Startup));
        }
        catch
        {
            // Last-resort guard — asset resolution must never block startup
            return (null, null);
        }
    }
}
