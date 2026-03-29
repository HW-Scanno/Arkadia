using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Arkadia.Themes;
using Xunit;

namespace Arkadia.Tests.Themes;

public sealed class ThemeManagerTests : IDisposable
{
    private readonly string _root;

    public ThemeManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"arkadia_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // --- helpers ---

    private string CreateThemeDir(string themeId)
    {
        var dir = Path.Combine(_root, themeId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteManifest(string themeDir, object manifest) =>
        File.WriteAllText(
            Path.Combine(themeDir, "theme.json"),
            JsonSerializer.Serialize(manifest));

    // --- tests ---

    [Fact]
    public void ValidThemeWithValidManifest_IsSelectable()
    {
        var dir = CreateThemeDir("my-theme");
        WriteManifest(dir, new { id = "my-theme", name = "My Theme" });

        var manager = new ThemeManager(_root);
        manager.Scan();

        var selectable = manager.GetSelectableThemes();
        Assert.Single(selectable);
        Assert.Equal("my-theme", selectable[0].ThemeId);
        Assert.True(selectable[0].IsSelectable);
        Assert.True(selectable[0].IsRuntimeValid);
        Assert.Equal("My Theme", selectable[0].Manifest!.Name);
    }

    [Fact]
    public void ThemeFolderWithoutManifest_IsRuntimeValidButNotSelectable()
    {
        CreateThemeDir("bare-theme");

        var manager = new ThemeManager(_root);
        manager.Scan();

        var runtimeValid = manager.GetRuntimeValidThemes();
        Assert.Single(runtimeValid);
        Assert.True(runtimeValid[0].IsRuntimeValid);
        Assert.False(runtimeValid[0].IsSelectable);
        Assert.Empty(manager.GetSelectableThemes());
    }

    [Fact]
    public void InvalidManifestJson_IsRuntimeValidButNotSelectable()
    {
        var dir = CreateThemeDir("broken-theme");
        File.WriteAllText(Path.Combine(dir, "theme.json"), "{ not: valid json }}}");

        var manager = new ThemeManager(_root);
        manager.Scan();

        var runtimeValid = manager.GetRuntimeValidThemes();
        Assert.Single(runtimeValid);
        Assert.True(runtimeValid[0].IsRuntimeValid);
        Assert.False(runtimeValid[0].IsSelectable);
        Assert.Null(runtimeValid[0].Manifest);
    }

    [Fact]
    public void ManifestMissingRequiredFields_IsNotSelectable()
    {
        var dir = CreateThemeDir("incomplete-theme");
        WriteManifest(dir, new { id = "incomplete-theme" }); // name missing

        var manager = new ThemeManager(_root);
        manager.Scan();

        Assert.Empty(manager.GetSelectableThemes());
        Assert.True(manager.GetRuntimeValidThemes()[0].IsRuntimeValid);
    }

    [Fact]
    public void ConfiguredActiveTheme_IsResolvedWhenSelectable()
    {
        var d1 = CreateThemeDir("my-theme");
        WriteManifest(d1, new { id = "my-theme", name = "My Theme" });
        var d2 = CreateThemeDir("default");
        WriteManifest(d2, new { id = "default", name = "Default" });

        var manager = new ThemeManager(_root, activeThemeId: "my-theme");
        manager.Scan();

        var active = manager.ResolveActiveTheme();
        Assert.NotNull(active);
        Assert.Equal("my-theme", active.ThemeId);
    }

    [Fact]
    public void ActiveTheme_FallsBackToDefaultWhenConfiguredIsMissing()
    {
        var dir = CreateThemeDir("default");
        WriteManifest(dir, new { id = "default", name = "Default" });

        var manager = new ThemeManager(_root, activeThemeId: "nonexistent-theme");
        manager.Scan();

        var active = manager.ResolveActiveTheme();
        Assert.NotNull(active);
        Assert.Equal("default", active.ThemeId);
    }

    [Fact]
    public void ActiveTheme_FallsBackToRuntimeValidDefault_WhenDefaultHasNoManifest()
    {
        CreateThemeDir("default"); // folder only, no theme.json

        var manager = new ThemeManager(_root, activeThemeId: "nonexistent-theme");
        manager.Scan();

        var active = manager.ResolveActiveTheme();
        Assert.NotNull(active);
        Assert.Equal("default", active.ThemeId);
        Assert.True(active.IsRuntimeValid);
        Assert.False(active.IsSelectable);
    }

    [Fact]
    public void MissingDefault_FailsSafelyWithNull()
    {
        CreateThemeDir("some-other-theme");

        var manager = new ThemeManager(_root, activeThemeId: "nonexistent-theme");
        manager.Scan();

        Assert.Null(manager.ResolveActiveTheme());
    }

    [Fact]
    public void NonExistentThemesRoot_FailsSafelyWithNoThemes()
    {
        var manager = new ThemeManager(Path.Combine(_root, "does-not-exist"));
        manager.Scan();

        Assert.Empty(manager.GetRuntimeValidThemes());
        Assert.Empty(manager.GetSelectableThemes());
        Assert.Null(manager.ResolveActiveTheme());
    }
}
