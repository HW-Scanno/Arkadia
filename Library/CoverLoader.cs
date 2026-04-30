using System;
using System.IO;
using Arkadia.Data;
using Avalonia.Media.Imaging;

namespace Arkadia.Library;

/// <summary>
/// Validates and loads cover art bitmaps defensively.
/// All methods are safe to call with arbitrary paths from the media store.
/// </summary>
public static class CoverLoader
{
    // ── File-level validation ─────────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="path"/> passes all file-level checks:
    /// known image extension, file exists, and non-zero size.
    /// No bitmap loading is attempted.
    /// </summary>
    internal static bool IsValidFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!MediaStore.ImageExtensions.Contains(Path.GetExtension(path))) return false;
        if (!File.Exists(path)) return false;
        return new FileInfo(path).Length > 0;
    }

    // ── Bitmap loading ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to load a cover image bitmap with full defensive validation.
    /// Returns null when the file is missing, zero-byte, has an unsupported extension,
    /// fails to decode, or decodes to a zero-dimension image.
    /// The caller owns the returned bitmap and must dispose it.
    /// </summary>
    public static Bitmap? TryLoad(string? path)
    {
        if (!IsValidFile(path)) return null;

        Bitmap? bmp = null;
        try
        {
            bmp = new Bitmap(path!);
            if (bmp.PixelSize.Width <= 0 || bmp.PixelSize.Height <= 0)
            {
                bmp.Dispose();
                return null;
            }
            return bmp;
        }
        catch
        {
            bmp?.Dispose();
            return null;
        }
    }
}
