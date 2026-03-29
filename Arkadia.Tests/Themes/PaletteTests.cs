using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Arkadia.Themes;
using Avalonia.Media;
using Xunit;

namespace Arkadia.Tests.Themes;

/// <summary>
/// Verifies palette parsing, validation, and descriptor exposure.
/// </summary>
public sealed class PaletteTests : IDisposable
{
    private readonly string _root;

    public PaletteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"arkadia_palette_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // --- helpers ---

    private string CreateTheme(string themeId, object manifest)
    {
        var dir = Path.Combine(_root, themeId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "theme.json"), JsonSerializer.Serialize(manifest));
        return dir;
    }

    // --- ThemePalette.Parse unit tests ---

    [Fact]
    public void Parse_NullRaw_ReturnsEmptyDictionary()
    {
        var result = ThemePalette.Parse(null);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_EmptyRaw_ReturnsEmptyDictionary()
    {
        var result = ThemePalette.Parse(new Dictionary<string, string>());
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ValidEntries_ParsedCorrectly()
    {
        var raw = new Dictionary<string, string>
        {
            ["background"] = "#1A1A2E",
            ["accent"]     = "#E94560",
        };

        var result = ThemePalette.Parse(raw);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("background"));
        Assert.True(result.ContainsKey("accent"));
        Assert.Equal(Color.Parse("#1A1A2E"), result["background"]);
        Assert.Equal(Color.Parse("#E94560"), result["accent"]);
    }

    [Fact]
    public void Parse_InvalidColorValue_EntryDropped()
    {
        var raw = new Dictionary<string, string>
        {
            ["background"] = "#1A1A2E",
            ["accent"]     = "not-a-color",
        };

        var result = ThemePalette.Parse(raw);

        Assert.Single(result);
        Assert.True(result.ContainsKey("background"));
        Assert.False(result.ContainsKey("accent"));
    }

    [Fact]
    public void Parse_UnsupportedKey_EntryDropped()
    {
        var raw = new Dictionary<string, string>
        {
            ["background"]   = "#1A1A2E",
            ["customBorder"] = "#FF0000", // not a v1 key
        };

        var result = ThemePalette.Parse(raw);

        Assert.Single(result);
        Assert.False(result.ContainsKey("customBorder"));
    }

    [Fact]
    public void Parse_AllInvalid_ReturnsEmptyDictionary()
    {
        var raw = new Dictionary<string, string>
        {
            ["background"] = "bad",
            ["accent"]     = "also-bad",
        };

        var result = ThemePalette.Parse(raw);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_AllSupportedKeys_AllAccepted()
    {
        var raw = new Dictionary<string, string>
        {
            ["background"]   = "#000000",
            ["surface"]      = "#111111",
            ["surfaceAlt"]   = "#222222",
            ["accent"]       = "#333333",
            ["textPrimary"]  = "#444444",
            ["textSecondary"]= "#555555",
            ["success"]      = "#66BB6A",
            ["warning"]      = "#FFA726",
            ["error"]        = "#EF5350",
            ["info"]         = "#42A5F5",
        };

        var result = ThemePalette.Parse(raw);

        Assert.Equal(10, result.Count);
    }

    // --- ThemeManager integration: descriptor exposes palette ---

    [Fact]
    public void Theme_WithoutPalette_IsStillSelectable()
    {
        CreateTheme("no-palette", new { id = "no-palette", name = "No Palette" });

        var manager = new ThemeManager(_root);
        manager.Scan();

        var theme = manager.ResolveActiveTheme();
        Assert.Null(theme); // no "default" theme — but selectable list is populated
        var selectable = manager.GetSelectableThemes();
        Assert.Single(selectable);
        Assert.Empty(selectable[0].Palette); // no palette → empty, not null
    }

    [Fact]
    public void Theme_WithValidPalette_PaletteExposedOnDescriptor()
    {
        CreateTheme("default", new
        {
            id      = "default",
            name    = "Default",
            palette = new { background = "#0D0D0D", accent = "#FF6B35" },
        });

        var manager = new ThemeManager(_root, "default");
        manager.Scan();

        var theme = manager.ResolveActiveTheme();
        Assert.NotNull(theme);
        Assert.Equal(2, theme!.Palette.Count);
        Assert.Equal(Color.Parse("#0D0D0D"), theme.Palette["background"]);
        Assert.Equal(Color.Parse("#FF6B35"), theme.Palette["accent"]);
    }

    [Fact]
    public void Theme_WithPartiallyInvalidPalette_OnlyValidEntriesKept()
    {
        CreateTheme("default", new
        {
            id      = "default",
            name    = "Default",
            palette = new { background = "#0D0D0D", accent = "GARBAGE", surface = "#1A1A2E" },
        });

        var manager = new ThemeManager(_root, "default");
        manager.Scan();

        var theme = manager.ResolveActiveTheme();
        Assert.NotNull(theme);
        Assert.Equal(2, theme!.Palette.Count);      // "accent" dropped
        Assert.False(theme.Palette.ContainsKey("accent"));
    }

    [Fact]
    public void Theme_WithCompletelyInvalidPalette_PaletteIsEmpty_ThemeStillSelectable()
    {
        CreateTheme("default", new
        {
            id      = "default",
            name    = "Default",
            palette = new { background = "bad", accent = "alsoBad" },
        });

        var manager = new ThemeManager(_root, "default");
        manager.Scan();

        var theme = manager.ResolveActiveTheme();
        Assert.NotNull(theme);          // theme still resolves
        Assert.Empty(theme!.Palette);   // palette is empty but theme is valid
    }
}
