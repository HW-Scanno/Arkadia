using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Themes;
using Arkadia.Themes.Assets;
using Xunit;

namespace Arkadia.Tests.Themes.Assets;

public sealed class ThemeAssetSelectorTests : IDisposable
{
    private readonly string _root;
    private readonly string _statePath;

    public ThemeAssetSelectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"arkadia_assets_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _statePath = Path.Combine(_root, "asset_state.json");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // --- helpers ---

    private ThemeDescriptor MakeTheme(string themeId)
    {
        var dir = Path.Combine(_root, "themes", themeId);
        Directory.CreateDirectory(dir);
        return new ThemeDescriptor { ThemeId = themeId, ThemeDirectory = dir, IsRuntimeValid = true, IsSelectable = true };
    }

    private static void CreateImage(ThemeDescriptor theme, string family, string filename)
    {
        var dir = Path.Combine(theme.ThemeDirectory, "images", family);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, filename), string.Empty);
    }

    private static void CreateSound(ThemeDescriptor theme, string family, string filename)
    {
        var dir = Path.Combine(theme.ThemeDirectory, "sounds", family);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, filename), string.Empty);
    }

    // --- enumeration ---

    [Fact]
    public void SplashImageFamily_OnlyEnumeratesPngFiles()
    {
        var theme = MakeTheme("t");
        CreateImage(theme, ThemeFamily.Image.Splash, "a.png");
        CreateImage(theme, ThemeFamily.Image.Splash, "b.png");
        CreateImage(theme, ThemeFamily.Image.Splash, "ignore.jpg"); // excluded

        var selector = new ThemeAssetSelector(_statePath);

        var p1 = selector.GetNextImage(theme, ThemeFamily.Image.Splash);
        var p2 = selector.GetNextImage(theme, ThemeFamily.Image.Splash);

        Assert.NotNull(p1); Assert.EndsWith(".png", p1);
        Assert.NotNull(p2); Assert.EndsWith(".png", p2);
    }

    [Fact]
    public void StartupSoundFamily_OnlyEnumeratesWavFiles()
    {
        var theme = MakeTheme("t");
        CreateSound(theme, ThemeFamily.Sound.Startup, "s.wav");
        CreateSound(theme, ThemeFamily.Sound.Startup, "t.wav");
        CreateSound(theme, ThemeFamily.Sound.Startup, "ignore.mp3"); // excluded

        var selector = new ThemeAssetSelector(_statePath);

        var p1 = selector.GetNextSound(theme, ThemeFamily.Sound.Startup);
        var p2 = selector.GetNextSound(theme, ThemeFamily.Sound.Startup);

        Assert.NotNull(p1); Assert.EndsWith(".wav", p1);
        Assert.NotNull(p2); Assert.EndsWith(".wav", p2);
    }

    [Fact]
    public void SoundFamilies_AreEnumerated_ByDirectory()
    {
        var theme = MakeTheme("t");
        CreateSound(theme, ThemeFamily.Sound.MouseClick, "click.wav");
        CreateSound(theme, ThemeFamily.Sound.MenuClick,  "menu.wav");
        CreateSound(theme, ThemeFamily.Sound.WindowOpen, "open.wav");

        var selector = new ThemeAssetSelector(_statePath);

        var click = selector.GetNextSound(theme, ThemeFamily.Sound.MouseClick);
        var menu  = selector.GetNextSound(theme, ThemeFamily.Sound.MenuClick);
        var open  = selector.GetNextSound(theme, ThemeFamily.Sound.WindowOpen);

        Assert.NotNull(click); Assert.Contains("mouse_click", click);
        Assert.NotNull(menu);  Assert.Contains("menu_click",  menu);
        Assert.NotNull(open);  Assert.Contains("window_open", open);
    }

    // --- shuffle cycle ---

    [Fact]
    public void ShuffleCycle_VisitsAllFiles_BeforeRepeat()
    {
        var theme = MakeTheme("t");
        CreateImage(theme, ThemeFamily.Image.Splash, "a.png");
        CreateImage(theme, ThemeFamily.Image.Splash, "b.png");
        CreateImage(theme, ThemeFamily.Image.Splash, "c.png");

        var selector = new ThemeAssetSelector(_statePath, new Random(42));
        var seen = new HashSet<string>();

        for (var i = 0; i < 3; i++)
        {
            var p = selector.GetNextImage(theme, ThemeFamily.Image.Splash);
            Assert.NotNull(p);
            seen.Add(p!);
        }

        Assert.Equal(3, seen.Count); // all 3 visited exactly once
    }

    [Fact]
    public void ShuffleCycle_Reshuffles_AfterExhaustion()
    {
        var theme = MakeTheme("t");
        CreateSound(theme, ThemeFamily.Sound.Startup, "a.wav");
        CreateSound(theme, ThemeFamily.Sound.Startup, "b.wav");

        var selector = new ThemeAssetSelector(_statePath);

        // Exhaust the 2-item cycle
        selector.GetNextSound(theme, ThemeFamily.Sound.Startup);
        selector.GetNextSound(theme, ThemeFamily.Sound.Startup);

        // Post-exhaustion call must return a valid path without throwing
        var result = selector.GetNextSound(theme, ThemeFamily.Sound.Startup);
        Assert.NotNull(result);
        Assert.EndsWith(".wav", result);
    }

    [Fact]
    public void ShuffleCycles_AreIndependent_PerFamily()
    {
        var theme = MakeTheme("t");
        CreateImage(theme, ThemeFamily.Image.Splash, "s1.png");
        CreateImage(theme, ThemeFamily.Image.Splash, "s2.png");
        CreateImage(theme, ThemeFamily.Image.Splash, "s3.png");
        CreateSound(theme, ThemeFamily.Sound.Startup, "x.wav");
        CreateSound(theme, ThemeFamily.Sound.Startup, "y.wav");
        CreateSound(theme, ThemeFamily.Sound.Startup, "z.wav");

        var selector = new ThemeAssetSelector(_statePath, new Random(7));

        // Advance splash cycle twice (index 0→2)
        var sp1 = selector.GetNextImage(theme, ThemeFamily.Image.Splash);
        var sp2 = selector.GetNextImage(theme, ThemeFamily.Image.Splash);

        // Exhaust the sound cycle independently
        var sounds = new HashSet<string?>();
        sounds.Add(selector.GetNextSound(theme, ThemeFamily.Sound.Startup));
        sounds.Add(selector.GetNextSound(theme, ThemeFamily.Sound.Startup));
        sounds.Add(selector.GetNextSound(theme, ThemeFamily.Sound.Startup));

        Assert.Equal(3, sounds.Count); // sound cycle ran through all 3 independently

        // Splash cycle continues from where it left off (index 2 → not yet reset)
        var sp3 = selector.GetNextImage(theme, ThemeFamily.Image.Splash);

        // All 3 splashes are distinct (one full cycle was used across the 3 calls)
        Assert.NotEqual(sp1, sp2);
        Assert.NotEqual(sp2, sp3);
        Assert.NotEqual(sp1, sp3);
    }

    // --- theme change ---

    [Fact]
    public void ThemeChange_ResetsAllFamilyCycles()
    {
        var themeA = MakeTheme("theme-a");
        CreateImage(themeA, ThemeFamily.Image.Splash, "a.png");
        CreateSound(themeA, ThemeFamily.Sound.Startup, "a.wav");

        var selector = new ThemeAssetSelector(_statePath);
        selector.GetNextImage(themeA, ThemeFamily.Image.Splash);
        selector.GetNextSound(themeA, ThemeFamily.Sound.Startup);

        var themeB = MakeTheme("theme-b");
        CreateImage(themeB, ThemeFamily.Image.Splash, "b1.png");
        CreateImage(themeB, ThemeFamily.Image.Splash, "b2.png");
        CreateSound(themeB, ThemeFamily.Sound.Startup, "b1.wav");
        CreateSound(themeB, ThemeFamily.Sound.Startup, "b2.wav");

        var sp1 = selector.GetNextImage(themeB, ThemeFamily.Image.Splash);
        var sp2 = selector.GetNextImage(themeB, ThemeFamily.Image.Splash);
        var sn1 = selector.GetNextSound(themeB, ThemeFamily.Sound.Startup);
        var sn2 = selector.GetNextSound(themeB, ThemeFamily.Sound.Startup);

        // Paths come from theme-b, not theme-a
        Assert.NotNull(sp1); Assert.Contains("theme-b", sp1);
        Assert.NotNull(sp2); Assert.Contains("theme-b", sp2);
        Assert.NotNull(sn1); Assert.Contains("theme-b", sn1);
        Assert.NotNull(sn2); Assert.Contains("theme-b", sn2);

        // First full cycle of each yields two distinct values
        Assert.NotEqual(sp1, sp2);
        Assert.NotEqual(sn1, sn2);
    }

    // --- safe fallback ---

    [Fact]
    public void MissingImageFamily_ReturnsNull()
    {
        var theme = MakeTheme("t"); // no images/splash dir

        var result = new ThemeAssetSelector(_statePath).GetNextImage(theme, ThemeFamily.Image.Splash);

        Assert.Null(result);
    }

    [Fact]
    public void MissingSoundFamily_ReturnsNull()
    {
        var theme = MakeTheme("t"); // no sounds/startup dir

        var result = new ThemeAssetSelector(_statePath).GetNextSound(theme, ThemeFamily.Sound.Startup);

        Assert.Null(result);
    }

    [Fact]
    public void CorruptedPersistentState_FailsSafely()
    {
        File.WriteAllText(_statePath, "{ this is: not valid json }}}");

        var theme = MakeTheme("t");
        CreateImage(theme, ThemeFamily.Image.Splash, "a.png");

        var result = new ThemeAssetSelector(_statePath).GetNextImage(theme, ThemeFamily.Image.Splash);

        Assert.NotNull(result);
    }
}
