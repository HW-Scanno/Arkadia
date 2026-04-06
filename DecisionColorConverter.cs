using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Arkadia;

/// <summary>Converts a planning decision string to a foreground brush.</summary>
public sealed class DecisionColorConverter : IValueConverter
{
    public static readonly DecisionColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = (value as string) switch
        {
            "include" => "#81C784",
            "defer"   => "#FFB74D",
            "skip"    => "#888899",
            _         => "#AAAACC",
        };
        return new SolidColorBrush(Color.Parse(hex));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
