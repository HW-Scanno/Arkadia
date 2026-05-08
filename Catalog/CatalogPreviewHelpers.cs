using System.IO;
using Avalonia.Media.Imaging;

namespace Arkadia;

internal static class CatalogPreviewHelpers
{
    /// <summary>
    /// Loads a <see cref="Bitmap"/> from <paramref name="filePath"/>.
    /// Returns null if the file does not exist, is unreadable, or is not a valid image.
    /// Never throws.
    /// </summary>
    internal static Bitmap? TryLoadBitmap(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try { return new Bitmap(filePath); }
        catch { return null; }
    }
}
