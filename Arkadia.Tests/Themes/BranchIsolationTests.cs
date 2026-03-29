using System;
using System.IO;
using System.Text.Json;
using Arkadia.Themes;
using Arkadia.Themes.Assets;
using Xunit;

namespace Arkadia.Tests.Themes;

/// <summary>
/// Verifies that the visual and audio theme branches are fully independent:
/// independent discovery, independent fallback, and independent shuffle-cycle state.
/// </summary>
public sealed class BranchIsolationTests : IDisposable
{
    private readonly string _root;
    private readonly string _statePath;

    public BranchIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"arkadia_branch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _statePath = Path.Combine(_root, "state.json");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // --- helpers ---

    private string VisualRoot => Path.Combine(_root, "themes", "visual");
    private string AudioRoot  => Path.Combine(_root, "themes", "audio");

    private ThemeDescriptor MakeTheme(string branch, string themeId)
    {
        var dir = Path.Combine(_root, "themes", branch, themeId);
        Directory.CreateDirectory(dir);
        return new ThemeDescriptor { ThemeId = themeId, ThemeDirectory = dir, IsRuntimeValid = true, IsSelectable = true };
    }

    private static void WriteManifest(string themeDir, string id, string name) =>
        File.WriteAllText(Path.Combine(themeDir, "theme.json"), JsonSerializer.Serialize(new { id, name }));

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

    // --- discovery isolation ---

    [Fact]
    public void VisualAndAudioThemes_AreDiscovered_Independently()
    {
        var vis = MakeTheme("visual", "vis-1");
        WriteManifest(vis.ThemeDirectory, "vis-1", "Visual One");
        var aud = MakeTheme("audio", "aud-1");
        WriteManifest(aud.ThemeDirectory, "aud-1", "Audio One");

        var visualManager = new ThemeManager(VisualRoot);
        var audioManager  = new ThemeManager(AudioRoot);
        visualManager.Scan();
        audioManager.Scan();

        // Each manager sees only its own branch
        Assert.Single(visualManager.GetSelectableThemes());
        Assert.Equal("vis-1", visualManager.GetSelectableThemes()[0].ThemeId);
        Assert.Single(audioManager.GetSelectableThemes());
        Assert.Equal("aud-1", audioManager.GetSelectableThemes()[0].ThemeId);
    }

    [Fact]
    public void Fallback_ToDefault_IsIndependent_PerBranch()
    {
        // Only a visual default exists — audio has nothing
        var visDefault = MakeTheme("visual", "default");
        WriteManifest(visDefault.ThemeDirectory, "default", "Default Visual");

        var visualManager = new ThemeManager(VisualRoot, "nonexistent-visual");
        var audioManager  = new ThemeManager(AudioRoot,  "nonexistent-audio");
        visualManager.Scan();
        audioManager.Scan();

        Assert.NotNull(visualManager.ResolveActiveTheme());
        Assert.Equal("default", visualManager.ResolveActiveTheme()!.ThemeId);
        Assert.Null(audioManager.ResolveActiveTheme()); // audio has no default → safe null
    }

    // --- shuffle-state branch isolation ---

    [Fact]
    public void VisualThemeChange_ResetsOnlyImageCycles_NotSoundCycles()
    {
        var visA = MakeTheme("visual", "vis-a");
        var visB = MakeTheme("visual", "vis-b");
        var audA = MakeTheme("audio",  "aud-a");
        CreateImage(visA, ThemeFamily.Image.Splash, "va.png");
        CreateImage(visB, ThemeFamily.Image.Splash, "vb.png");
        CreateSound(audA, ThemeFamily.Sound.Startup, "a.wav");

        var selector = new ThemeAssetSelector(_statePath);
        selector.GetNextSound(audA, ThemeFamily.Sound.Startup); // establishes AudioThemeId = "aud-a"
        selector.GetNextImage(visA, ThemeFamily.Image.Splash);  // establishes VisualThemeId = "vis-a"

        // Change visual theme — must only touch image state
        selector.GetNextImage(visB, ThemeFamily.Image.Splash);

        var state = JsonSerializer.Deserialize<ShuffleState>(File.ReadAllText(_statePath))!;
        Assert.Equal("vis-b", state.VisualThemeId); // visual updated
        Assert.Equal("aud-a", state.AudioThemeId);  // audio untouched
        Assert.NotEmpty(state.SoundCycles);          // audio cycles survive
    }

    [Fact]
    public void AudioThemeChange_ResetsOnlySoundCycles_NotImageCycles()
    {
        var visA = MakeTheme("visual", "vis-a");
        var audA = MakeTheme("audio",  "aud-a");
        var audB = MakeTheme("audio",  "aud-b");
        CreateImage(visA, ThemeFamily.Image.Splash, "va.png");
        CreateSound(audA, ThemeFamily.Sound.Startup, "a.wav");
        CreateSound(audB, ThemeFamily.Sound.Startup, "b.wav");

        var selector = new ThemeAssetSelector(_statePath);
        selector.GetNextImage(visA, ThemeFamily.Image.Splash);  // establishes VisualThemeId = "vis-a"
        selector.GetNextSound(audA, ThemeFamily.Sound.Startup); // establishes AudioThemeId = "aud-a"

        // Change audio theme — must only touch sound state
        selector.GetNextSound(audB, ThemeFamily.Sound.Startup);

        var state = JsonSerializer.Deserialize<ShuffleState>(File.ReadAllText(_statePath))!;
        Assert.Equal("vis-a", state.VisualThemeId); // visual untouched
        Assert.Equal("aud-b", state.AudioThemeId);  // audio updated
        Assert.NotEmpty(state.ImageCycles);          // image cycles survive
    }
}
