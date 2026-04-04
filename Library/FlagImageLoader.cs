using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;

namespace Arkadia.Library;

/// <summary>
/// Resolves language flag images from:
///   themes/visual/{theme}/flags/{code}.png
/// Falls back to flags/unknown.png, then null (no crash).
/// Results are cached for the lifetime of the process.
/// </summary>
public static class FlagImageLoader
{
    private static readonly Dictionary<string, Bitmap?> _cache = new();

    public static Bitmap? Load(string themeDirectory, string languageCode)
    {
        var key = $"{themeDirectory}|{languageCode}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var flagDir = Path.Combine(themeDirectory, "flags");
        var primary = Path.Combine(flagDir, $"{languageCode.ToLowerInvariant()}.png");
        if (File.Exists(primary)) return Cache(key, TryLoad(primary));

        var fallback = Path.Combine(flagDir, "unknown.png");
        if (File.Exists(fallback)) return Cache(key, TryLoad(fallback));

        return Cache(key, null);
    }

    private static Bitmap? Cache(string key, Bitmap? bmp) { _cache[key] = bmp; return bmp; }

    private static Bitmap? TryLoad(string path)
    {
        try   { return new Bitmap(path); }
        catch { return null; }
    }
}
