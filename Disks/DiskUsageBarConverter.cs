using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Arkadia.Disks;

/// <summary>
/// Converts a usage ratio (0.0–1.0) to a pixel width for the usage bar fill.
/// Track width is fixed at 66 px (90 px column minus 2×8 margin).
/// </summary>
public sealed class DiskUsageBarConverter : IValueConverter
{
    public static readonly DiskUsageBarConverter Instance = new();

    private const double TrackWidth = 66.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double ratio)
            return Math.Clamp(ratio, 0, 1) * TrackWidth;
        return 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
