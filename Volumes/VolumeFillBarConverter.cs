using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Arkadia.Volumes;

/// <summary>
/// Converts a FillRatio (0.0–1.0) to a single SolidColorBrush interpolated
/// through purple → cyan → green → yellow → orange → red anchor points.
/// </summary>
public sealed class VolumeFillBarConverter : IValueConverter
{
    public static readonly VolumeFillBarConverter Instance = new();

    // (ratio, R, G, B) — color anchors
    private static readonly (double t, byte r, byte g, byte b)[] Anchors =
    [
        (0.00, 0x7B, 0x68, 0xEE),  // Purple
        (0.10, 0x7B, 0x68, 0xEE),  // Purple (hold through 10 %)
        (0.20, 0x26, 0xC6, 0xDA),  // Cyan
        (0.50, 0x4C, 0xAF, 0x50),  // Green
        (0.70, 0xFF, 0xB3, 0x00),  // Yellow
        (0.85, 0xFF, 0x6D, 0x00),  // Orange
        (1.00, 0xE5, 0x39, 0x35),  // Red
    ];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double t = value is double d ? Math.Clamp(d, 0.0, 1.0) : 0.0;

        // Find the two surrounding anchors
        int hi = 1;
        while (hi < Anchors.Length - 1 && Anchors[hi].t < t) hi++;
        var lo   = Anchors[hi - 1];
        var high = Anchors[hi];

        double span = high.t - lo.t;
        double f    = span < 1e-9 ? 0.0 : (t - lo.t) / span;

        byte r = (byte)Math.Round(lo.r + f * (high.r - lo.r));
        byte g = (byte)Math.Round(lo.g + f * (high.g - lo.g));
        byte b = (byte)Math.Round(lo.b + f * (high.b - lo.b));

        return new SolidColorBrush(new Color(255, r, g, b));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
