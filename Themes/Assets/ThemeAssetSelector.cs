using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Arkadia.Themes.Assets;

/// <summary>
/// Enumerates assets for a theme family and advances a persistent shuffle cycle per family.
/// <para>
/// Visual themes supply images: <c>themes/visual/&lt;id&gt;/images/&lt;family&gt;/</c> (.png).<br/>
/// Audio themes supply sounds:  <c>themes/audio/&lt;id&gt;/sounds/&lt;family&gt;/</c>  (.wav).
/// </para>
/// Each branch tracks its active theme id independently in the persisted state:
/// changing the visual theme resets only image cycles; changing the audio theme resets only sound cycles.
/// </summary>
public sealed class ThemeAssetSelector
{
    private readonly string _statePath;
    private readonly Random _random;

    /// <param name="statePath">Path to the JSON file used to persist state between launches.</param>
    /// <param name="random">Optional seeded Random; uses Random.Shared when null.</param>
    public ThemeAssetSelector(string statePath, Random? random = null)
    {
        _statePath = statePath;
        _random = random ?? Random.Shared;
    }

    /// <summary>
    /// Returns the next .png path for <paramref name="family"/> from the visual theme, or null if none exist.
    /// Resets image-family cycles if the visual theme has changed; audio cycles are unaffected.
    /// </summary>
    public string? GetNextImage(ThemeDescriptor theme, string family)
    {
        var state = LoadState();
        if (state.VisualThemeId != theme.ThemeId)
        {
            state.VisualThemeId = theme.ThemeId;
            state.ImageCycles   = new Dictionary<string, FamilyCycleState>();
        }
        var dir = Path.Combine(theme.ThemeDirectory, "images", family);
        var file = Advance(EnumerateFiles(dir, "*.png"), family, state.ImageCycles);
        SaveState(state);
        return file is null ? null : Path.Combine(dir, file);
    }

    /// <summary>
    /// Returns the next .wav path for <paramref name="family"/> from the audio theme, or null if none exist.
    /// Resets sound-family cycles if the audio theme has changed; image cycles are unaffected.
    /// </summary>
    public string? GetNextSound(ThemeDescriptor theme, string family)
    {
        var state = LoadState();
        if (state.AudioThemeId != theme.ThemeId)
        {
            state.AudioThemeId = theme.ThemeId;
            state.SoundCycles  = new Dictionary<string, FamilyCycleState>();
        }
        var dir = Path.Combine(theme.ThemeDirectory, "sounds", family);
        var file = Advance(EnumerateFiles(dir, "*.wav"), family, state.SoundCycles);
        SaveState(state);
        return file is null ? null : Path.Combine(dir, file);
    }

    // Advances the named family's cycle within `cycles` (mutated in place) and returns the next filename.
    private string? Advance(
        IReadOnlyList<string> files,
        string key,
        Dictionary<string, FamilyCycleState> cycles)
    {
        if (files.Count == 0)
            return null;

        if (!cycles.TryGetValue(key, out var cycle))
            cycle = new FamilyCycleState();

        var order = cycle.Order;
        var index = cycle.Index;

        if (order.Count != files.Count)
        {
            // File set changed (or first run) — rebuild from current files
            order = Shuffle(files.ToList());
            index = 0;
        }
        else if (index >= order.Count)
        {
            // Cycle exhausted — reshuffle for the next round
            order = Shuffle(order);
            index = 0;
        }

        cycles[key] = new FamilyCycleState { Order = order, Index = index + 1 };
        return order[index];
    }

    private List<string> Shuffle(List<string> source)
    {
        var list = new List<string>(source);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    private static IReadOnlyList<string> EnumerateFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir))
            return [];
        return Directory.EnumerateFiles(dir, pattern)
            .Select(f => Path.GetFileName(f)!)
            .ToList();
    }

    private ShuffleState LoadState()
    {
        if (!File.Exists(_statePath))
            return new ShuffleState();
        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<ShuffleState>(json) ?? new ShuffleState();
        }
        catch (Exception)
        {
            return new ShuffleState();
        }
    }

    private void SaveState(ShuffleState state)
    {
        var dir = Path.GetDirectoryName(_statePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
    }
}
