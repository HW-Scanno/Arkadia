using System.IO;
using Avalonia.Media.Imaging;

namespace Arkadia.Systems;

/// <summary>
/// Resolves platform images from:
///   themes/visual/{theme}/systemimages/{platformId}.png
/// Falls back to systemimages/noimage.png, then null (no crash).
/// </summary>
public static class SystemImageLoader
{
    public static Bitmap? Load(string themeDirectory, string platformId)
    {
        var imageDir = Path.Combine(themeDirectory, "systemimages");

        var primary  = Path.Combine(imageDir, $"{platformId}.png");
        if (File.Exists(primary))
            return TryLoad(primary);

        var fallback = Path.Combine(imageDir, "noimage.png");
        if (File.Exists(fallback))
            return TryLoad(fallback);

        return null;
    }

    private static Bitmap? TryLoad(string path)
    {
        try   { return new Bitmap(path); }
        catch { return null; }
    }
}
