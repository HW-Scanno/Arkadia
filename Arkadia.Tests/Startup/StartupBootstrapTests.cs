using System;
using System.IO;
using System.Text.Json;
using Arkadia.Startup;
using Xunit;

namespace Arkadia.Tests.Startup;

public sealed class StartupBootstrapTests : IDisposable
{
    private readonly string _root;

    public StartupBootstrapTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"arkadia_boot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // --- helpers ---

    private string ThemesRoot => Path.Combine(_root, "themes");
    private string ConfigPath  => Path.Combine(_root, "appconfig.json");
    private string StatePath   => Path.Combine(_root, "startup_state.json");

    private void WriteConfig(string? visualThemeId = null, string? audioThemeId = null) =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
        {
            activeVisualThemeId = visualThemeId ?? "default",
            activeAudioThemeId  = audioThemeId  ?? "default",
        }));

    /// <param name="branch">"visual" or "audio"</param>
    private string CreateTheme(string branch, string themeId)
    {
        var dir = Path.Combine(ThemesRoot, branch, themeId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "theme.json"),
            JsonSerializer.Serialize(new { id = themeId, name = themeId }));
        return dir;
    }

    private static void CreateAsset(string themeDir, string subPath, string filename)
    {
        var dir = Path.Combine(themeDir, subPath);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, filename), string.Empty);
    }

    // --- tests ---

    [Fact]
    public void NoThemesDirectory_ReturnsBothNull()
    {
        var (splash, sound) = StartupBootstrap.Resolve(ThemesRoot, ConfigPath, StatePath);

        Assert.Null(splash);
        Assert.Null(sound);
    }

    [Fact]
    public void EmptyThemesRoot_ReturnsBothNull()
    {
        Directory.CreateDirectory(ThemesRoot); // themes/ exists but no visual/ or audio/ subdirs

        var (splash, sound) = StartupBootstrap.Resolve(ThemesRoot, ConfigPath, StatePath);

        Assert.Null(splash);
        Assert.Null(sound);
    }

    [Fact]
    public void VisualTheme_NoAssets_ReturnsNullSplash()
    {
        CreateTheme("visual", "my-visual");
        WriteConfig(visualThemeId: "my-visual");

        var (splash, sound) = StartupBootstrap.Resolve(ThemesRoot, ConfigPath, StatePath);

        Assert.Null(splash);
        Assert.Null(sound);
    }

    [Fact]
    public void VisualTheme_WithSplash_ReturnsSplashPath()
    {
        var dir = CreateTheme("visual", "my-visual");
        WriteConfig(visualThemeId: "my-visual");
        CreateAsset(dir, Path.Combine("images", "splash"), "splash.png");

        var (splash, sound) = StartupBootstrap.Resolve(ThemesRoot, ConfigPath, StatePath);

        Assert.NotNull(splash);
        Assert.EndsWith("splash.png", splash);
        Assert.Null(sound); // no audio theme
    }

    [Fact]
    public void AudioTheme_WithStartupSound_ReturnsSoundPath()
    {
        var dir = CreateTheme("audio", "my-audio");
        WriteConfig(audioThemeId: "my-audio");
        CreateAsset(dir, Path.Combine("sounds", "startup"), "boot.wav");

        var (splash, sound) = StartupBootstrap.Resolve(ThemesRoot, ConfigPath, StatePath);

        Assert.Null(splash); // no visual theme
        Assert.NotNull(sound);
        Assert.EndsWith("boot.wav", sound);
    }

    [Fact]
    public void SplashComesFromVisualBranch_SoundFromAudioBranch()
    {
        var visDir = CreateTheme("visual", "my-visual");
        var audDir = CreateTheme("audio",  "my-audio");
        WriteConfig(visualThemeId: "my-visual", audioThemeId: "my-audio");
        CreateAsset(visDir, Path.Combine("images", "splash"), "vis_splash.png");
        CreateAsset(audDir, Path.Combine("sounds", "startup"), "aud_sound.wav");

        var (splash, sound) = StartupBootstrap.Resolve(ThemesRoot, ConfigPath, StatePath);

        Assert.NotNull(splash); Assert.Contains("visual", splash);
        Assert.NotNull(sound);  Assert.Contains("audio",  sound);
    }

    [Fact]
    public void VisualFallback_ToDefault_WhenConfiguredThemeMissing()
    {
        var dir = CreateTheme("visual", "default");
        WriteConfig(visualThemeId: "nonexistent-visual");
        CreateAsset(dir, Path.Combine("images", "splash"), "splash.png");

        var (splash, _) = StartupBootstrap.Resolve(ThemesRoot, ConfigPath, StatePath);

        Assert.NotNull(splash);
        Assert.Contains("default", splash);
    }

    [Fact]
    public void AudioFallback_ToDefault_WhenConfiguredThemeMissing()
    {
        var dir = CreateTheme("audio", "default");
        WriteConfig(audioThemeId: "nonexistent-audio");
        CreateAsset(dir, Path.Combine("sounds", "startup"), "startup.wav");

        var (_, sound) = StartupBootstrap.Resolve(ThemesRoot, ConfigPath, StatePath);

        Assert.NotNull(sound);
        Assert.Contains("default", sound);
    }

    [Fact]
    public void MissingAudioBranch_DoesNotAffect_VisualSplash()
    {
        var visDir = CreateTheme("visual", "my-visual");
        WriteConfig(visualThemeId: "my-visual", audioThemeId: "my-audio");
        CreateAsset(visDir, Path.Combine("images", "splash"), "splash.png");
        // no audio branch

        var (splash, sound) = StartupBootstrap.Resolve(ThemesRoot, ConfigPath, StatePath);

        Assert.NotNull(splash); // visual unaffected
        Assert.Null(sound);     // audio missing → null, safe
    }

    // --- sound player safety ---

    [Fact]
    public void TryPlay_Null_DoesNotThrow()
    {
        var ex = Record.Exception(() => StartupSoundPlayer.TryPlay(null));
        Assert.Null(ex);
    }

    [Fact]
    public void TryPlay_MissingFile_DoesNotThrow()
    {
        var ex = Record.Exception(() => StartupSoundPlayer.TryPlay(Path.Combine(_root, "missing.wav")));
        Assert.Null(ex);
    }
}
