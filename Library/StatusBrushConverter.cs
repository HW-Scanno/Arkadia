using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Arkadia.Library;

/// <summary>
/// Maps a release status string (case-insensitive) to the standard Arkadia status foreground brush.
/// Authoritative source for status colors — same palette as <see cref="LibraryEntry.StatusBrush"/>.
/// </summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = (value as string)?.ToLowerInvariant() switch
        {
            "present"  => "#4CAF50",
            "pending"  => "#FFD54F",
            "missing"  => "#FFA726",
            "outdated" => "#FF8A65",
            "lost"     => "#EF5350",
            "unwanted" => "#9E9E9E",
            _          => "#888899",
        };
        return new SolidColorBrush(Color.Parse(hex));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
