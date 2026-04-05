using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Arkadia.Volumes;

/// <summary>
/// Converts a fill ratio (0.0–1.0) to a pixel width for the fill bar.
/// Track width is fixed at 56 px (80 px column minus 2×8 margin).
/// </summary>
public sealed class VolumeFillBarConverter : IValueConverter
{
    public static readonly VolumeFillBarConverter Instance = new();

    private const double TrackWidth = 56.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double ratio)
            return Math.Clamp(ratio, 0, 1) * TrackWidth;
        return 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
