using System;
using System.IO;
using SkiaSharp;

namespace Arkadia.Systems;

public static class PlatformImageCache
{
    public static string SourceFileName(string platformId, string role)
        => $"{platformId}-{role}_source.png";

    public static string CachedFileName(string platformId, string role, int width, int height)
        => $"{platformId}-{role}_cached_{width}x{height}.png";

    public static string CachedWidthFileName(string platformId, string role, int width)
        => $"{platformId}-{role}_cached_w{width}.png";

    /// <summary>
    /// Generates all size variants (WxH and width-constrained) from the given source file.
    /// Output files are written to the same directory as sourcePath.
    /// </summary>
    public static void GenerateCachedVariants(string sourcePath, string platformId, string role)
    {
        if (!File.Exists(sourcePath)) return;
        var imageDir = Path.GetDirectoryName(sourcePath)!;

        foreach (var (w, h) in PlatformImageSizes.All)
        {
            var outputPath = Path.Combine(imageDir, CachedFileName(platformId, role, w, h));
            GenerateSingle(sourcePath, outputPath, w, h);
        }

        foreach (var w in PlatformImageSizes.AllWidthConstrained)
        {
            var outputPath = Path.Combine(imageDir, CachedWidthFileName(platformId, role, w));
            GenerateSingleWidthConstrained(sourcePath, outputPath, w);
        }
    }

    /// <summary>
    /// Resizes sourcePath to fit within width×height (preserving aspect ratio, never upscaling),
    /// encodes as PNG at highest quality, and writes to outputPath. Always overwrites.
    /// </summary>
    public static void GenerateSingle(string sourcePath, string outputPath, int width, int height)
    {
        using var original = SKBitmap.Decode(sourcePath);
        if (original is null) return;

        var (tw, th) = FitWithin(original.Width, original.Height, width, height);
        var info     = new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var scaled = original.Resize(info, SKFilterQuality.High);
        if (scaled is null) return;

        using var image  = SKImage.FromBitmap(scaled);
        using var data   = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Resizes sourcePath to at most maxWidth pixels wide (preserving aspect ratio, never upscaling),
    /// encodes as PNG at highest quality, and writes to outputPath. Always overwrites.
    /// </summary>
    public static void GenerateSingleWidthConstrained(string sourcePath, string outputPath, int maxWidth)
    {
        using var original = SKBitmap.Decode(sourcePath);
        if (original is null) return;

        var (tw, th) = FitWidth(original.Width, original.Height, maxWidth);
        var info     = new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var scaled = original.Resize(info, SKFilterQuality.High);
        if (scaled is null) return;

        using var image  = SKImage.FromBitmap(scaled);
        using var data   = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write);
        data.SaveTo(stream);
    }

    private static (int W, int H) FitWithin(int srcW, int srcH, int maxW, int maxH)
    {
        if (srcW <= 0 || srcH <= 0) return (maxW, maxH);
        if (srcW <= maxW && srcH <= maxH) return (srcW, srcH);
        var ratio = Math.Min((double)maxW / srcW, (double)maxH / srcH);
        return (Math.Max(1, (int)(srcW * ratio)), Math.Max(1, (int)(srcH * ratio)));
    }

    private static (int W, int H) FitWidth(int srcW, int srcH, int maxW)
    {
        if (srcW <= 0 || srcH <= 0) return (maxW, maxW);
        if (srcW <= maxW) return (srcW, srcH);
        var ratio = (double)maxW / srcW;
        return (maxW, Math.Max(1, (int)Math.Round(srcH * ratio)));
    }
}
