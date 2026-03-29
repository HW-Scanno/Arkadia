using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Arkadia.Themes;

/// <summary>
/// Scans the themes root directory, validates each theme, and resolves the active theme.
/// </summary>
public sealed class ThemeManager
{
    private readonly string _themesRoot;
    private readonly string _activeThemeId;
    private List<ThemeDescriptor> _themes = [];

    /// <param name="themesRoot">Absolute path to the themes root folder (e.g. Arkadia/themes/).</param>
    /// <param name="activeThemeId">Configured active theme id; defaults to "default".</param>
    public ThemeManager(string themesRoot, string activeThemeId = "default")
    {
        _themesRoot = themesRoot;
        _activeThemeId = activeThemeId;
    }

    /// <summary>Scans <see cref="_themesRoot"/> and populates the internal theme list.</summary>
    public void Scan()
    {
        _themes = [];

        if (!Directory.Exists(_themesRoot))
            return;

        foreach (var dir in Directory.EnumerateDirectories(_themesRoot))
        {
            var themeId = Path.GetFileName(dir);
            var manifest = TryLoadManifest(dir);
            var isSelectable = manifest is { Id.Length: > 0, Name.Length: > 0 };

            _themes.Add(new ThemeDescriptor
            {
                ThemeId = themeId,
                ThemeDirectory = dir,
                IsRuntimeValid = true,
                IsSelectable = isSelectable,
                Manifest = manifest,
                Palette = ThemePalette.Parse(manifest?.Palette),
            });
        }
    }

    /// <summary>All themes whose folder exists, regardless of theme.json state.</summary>
    public IReadOnlyList<ThemeDescriptor> GetRuntimeValidThemes() =>
        _themes.Where(t => t.IsRuntimeValid).ToList();

    /// <summary>Themes with a valid theme.json containing at least id and name.</summary>
    public IReadOnlyList<ThemeDescriptor> GetSelectableThemes() =>
        _themes.Where(t => t.IsSelectable).ToList();

    /// <summary>
    /// Resolves the active theme using this priority:
    /// <list type="number">
    ///   <item>Configured active theme id, if selectable.</item>
    ///   <item>"default" theme, if runtime-valid (folder exists).</item>
    ///   <item>null — no usable theme found; caller must handle safely.</item>
    /// </list>
    /// </summary>
    public ThemeDescriptor? ResolveActiveTheme()
    {
        var configured = _themes.FirstOrDefault(t => t.ThemeId == _activeThemeId && t.IsSelectable);
        if (configured is not null)
            return configured;

        return _themes.FirstOrDefault(t => t.ThemeId == "default" && t.IsRuntimeValid);
    }

    private static ThemeManifest? TryLoadManifest(string themeDir)
    {
        var path = Path.Combine(themeDir, "theme.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ThemeManifest>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
